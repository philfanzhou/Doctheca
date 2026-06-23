namespace Ruoyu.Study.DocRetrieval.Service;

/// <summary>
/// Metadata for an image extracted from a MinerU ZIP and uploaded to S3.
/// </summary>
/// <param name="ImageName">Original image file name (e.g., "abc123.jpg")</param>
/// <param name="S3Path">Full S3 object path (e.g., "documents/mineru/task-xyz/abc123.jpg")</param>
/// <param name="ContentType">MIME type (e.g., "image/png" or "image/jpeg")</param>
/// <param name="FileSize">File size in bytes</param>
public record ImageMetadata(string ImageName, string S3Path, string ContentType, long FileSize);
