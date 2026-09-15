using System.Buffers.Text;
using System.Text.Json;

namespace Admin.Host.Identity;

/// <summary>Reads a JWT's claims for display. Nothing here validates a signature; the platform does that.</summary>
public static class JwtPayload
{
    public static JsonElement Decode(string jwt)
    {
        string[] parts = jwt.Split('.');

        if (parts.Length != 3)
        {
            throw new FormatException("A JWT has three dot-separated parts.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));

            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new FormatException("The JWT payload is not JSON.", e);
        }
    }
}
