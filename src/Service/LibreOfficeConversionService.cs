using Microsoft.Extensions.Logging;

namespace Ruoyu.Study.DocRetrieval.Service;

/// <summary>
/// Service for converting non-PDF files (DOCX, PPTX, etc.) to PDF
/// using LibreOffice headless mode.
/// </summary>
public interface IFileConversionService
{
    /// <summary>
    /// Check if LibreOffice is available for conversion.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Convert a non-PDF file stream to PDF.
    /// Returns the converted PDF stream, or null if conversion fails.
    /// </summary>
    Task<Stream?> ConvertToPdfAsync(Stream sourceStream, string fileName, CancellationToken ct = default);
}

public class LibreOfficeConversionService : IFileConversionService
{
    private readonly ILogger<LibreOfficeConversionService> _logger;
    private readonly string _libreOfficePath;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);
    private readonly bool _isAvailable;

    public LibreOfficeConversionService(ILogger<LibreOfficeConversionService> logger)
    {
        _logger = logger;

        // Try common LibreOffice paths
        var paths = new[] { "libreoffice", "/usr/bin/libreoffice", "soffice", "/usr/bin/soffice" };
        _libreOfficePath = paths.FirstOrDefault(p =>
        {
            try
            {
                if (Path.IsPathRooted(p))
                    return File.Exists(p);

                // Check if command is available via which
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = p,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit(5000);
                return process?.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }) ?? "libreoffice";

        _isAvailable = CheckAvailability();
        if (_isAvailable)
        {
            _logger.LogInformation("LibreOffice found at: {Path}", _libreOfficePath);
        }
        else
        {
            _logger.LogWarning("LibreOffice not found. Non-PDF file conversion will not be available.");
        }
    }

    public bool IsAvailable => _isAvailable;

    public async Task<Stream?> ConvertToPdfAsync(Stream sourceStream, string fileName, CancellationToken ct = default)
    {
        if (!_isAvailable)
        {
            throw new InvalidOperationException(
                "LibreOffice is not installed or not available. Cannot convert non-PDF files. " +
                "Please install LibreOffice to enable DOCX/PPTX to PDF conversion.");
        }

        var workDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            // Write source file to temp directory
            var sourcePath = Path.Combine(workDir, fileName);
            using (var fs = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                sourceStream.Position = 0;
                await sourceStream.CopyToAsync(fs, ct);
            }

            _logger.LogInformation("Converting {FileName} to PDF using LibreOffice", fileName);

            // Run LibreOffice headless conversion
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _libreOfficePath,
                Arguments = $"--headless --convert-to pdf --outdir \"{workDir}\" \"{sourcePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start LibreOffice process");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            var exited = process.WaitForExit((int)_timeout.TotalMilliseconds);
            if (!exited)
            {
                try { process.Kill(true); } catch { /* ignore */ }
                throw new TimeoutException($"LibreOffice conversion timed out after {_timeout.TotalSeconds}s for {fileName}");
            }

            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                _logger.LogError("LibreOffice conversion failed for {FileName}. Exit code: {ExitCode}. Error: {Error}",
                    fileName, process.ExitCode, stderr);
                return null;
            }

            // Find the converted PDF file
            var pdfFileName = Path.GetFileNameWithoutExtension(fileName) + ".pdf";
            var pdfPath = Path.Combine(workDir, pdfFileName);

            if (!File.Exists(pdfPath))
            {
                // Try case-insensitive search
                var pdfFile = Directory.GetFiles(workDir, "*.pdf").FirstOrDefault();
                if (pdfFile == null)
                {
                    _logger.LogError("LibreOffice conversion produced no PDF output for {FileName}", fileName);
                    return null;
                }
                pdfPath = pdfFile;
            }

            // Read the PDF into memory
            var pdfBytes = await File.ReadAllBytesAsync(pdfPath, ct);
            _logger.LogInformation("Successfully converted {FileName} to PDF ({Size} bytes)", fileName, pdfBytes.Length);

            return new MemoryStream(pdfBytes);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not TimeoutException)
        {
            _logger.LogError(ex, "Error converting {FileName} to PDF", fileName);
            return null;
        }
        finally
        {
            // Clean up temp directory
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp directory: {Dir}", workDir);
            }
        }
    }

    private bool CheckAvailability()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _libreOfficePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process == null) return false;

            process.WaitForExit(10000);
            var output = process.StandardOutput.ReadToEnd();
            _logger.LogInformation("LibreOffice version: {Version}", output.Trim());
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LibreOffice availability check failed");
            return false;
        }
    }
}
