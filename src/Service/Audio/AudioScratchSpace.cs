namespace Doctheca.Service.Audio;

/// <summary>Request-owned seekable storage with an unpredictable name, removed on every disposal path.</summary>
public class AudioScratchSpace
{
    public virtual Stream Open() => new FileStream(Path.Combine(Path.GetTempPath(), "doctheca-audio-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
        FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan);
}
