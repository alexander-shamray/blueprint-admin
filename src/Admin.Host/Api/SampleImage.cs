namespace Admin.Host.Api;

/// <summary>
/// The image the console uploads where run-locally.md uploads <c>.\desk.png</c> ("Attach a product image by hand"):
/// a real one-pixel PNG, because the store sniffs the bytes it is given and refuses what is not an image
/// (blueprint-backend <c>ImageAttachOutcome.NotAnImage</c>), so a placeholder of any other bytes would fail the publish.
/// <see cref="SizeBytes"/> is <c>$size</c> there: the signed request names the file's own length, and the store checks it.
/// </summary>
public static class SampleImage
{
    public const string ContentType = "image/png";

    /// <summary>What the document calls the file; the transcript's curl names it as <c>-InFile</c> does.</summary>
    public const string FileName = "desk.png";

    private const string Base64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGM4MS0FAAPsAcMKNRFCAAAAAElFTkSuQmCC";

    public static ReadOnlyMemory<byte> Bytes { get; } = Convert.FromBase64String(Base64);

    public static long SizeBytes => Bytes.Length;
}
