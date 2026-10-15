namespace Admin.Host.Api;

/// <summary>
/// The request bodies run-locally.md (workspace root, "Call the APIs") sends, keyed by operationId.
/// The zero Guids stand for "a fresh commandId" and "a product or order id you have"; the SPA
/// replaces <c>commandId</c> on every send. Update these when that document changes.
/// </summary>
public static class RunLocallyExamples
{
    /// <summary>run-locally.md, "Quote a basket".</summary>
    public const string Quote = """
        {
          "currency": "EUR",
          "lines": [ { "productId": "00000000-0000-0000-0000-000000000000", "quantity": 1 } ]
        }
        """;

    /// <summary>
    /// run-locally.md, "Attach a product image by hand": the content type and the file's own length. The length is
    /// <see cref="SampleImage.SizeBytes"/>, the image the console sends where the document sends <c>.\desk.png</c>.
    /// </summary>
    public static string ProductImageUpload => $$"""
        {
          "contentType": "{{SampleImage.ContentType}}",
          "sizeBytes": {{SampleImage.SizeBytes}}
        }
        """;

    /// <summary>run-locally.md, "Saved delivery address".</summary>
    public const string SavedAddress = """
        {
          "line1": "1 High Street",
          "line2": null,
          "city": "Leeds",
          "postalCode": "LS1 1AA",
          "country": "GB"
        }
        """;

    private static readonly Dictionary<string, string> Bodies = new(StringComparer.Ordinal)
    {
        // "Publish a product".
        ["PublishProduct"] = """
            {
              "commandId": "00000000-0000-0000-0000-000000000000",
              "name": "Walnut desk",
              "amount": 19.99,
              "currency": "EUR"
            }
            """,

        // "Place an order".
        ["PlaceOrder"] = """
            {
              "commandId": "00000000-0000-0000-0000-000000000000",
              "items": [ { "productId": "00000000-0000-0000-0000-000000000000", "quantity": 1 } ],
              "shippingAddress": { "line1": "1 Test Street", "city": "Almaty", "postalCode": "050000", "country": "KZ" },
              "currency": "EUR"
            }
            """,

        // "Stock for a product, and setting it".
        ["SetOnHand"] = """
            {
              "onHand": 10
            }
            """,

        // "Cancel".
        ["CancelOrder"] = """
            {
              "reason": "customer_request"
            }
            """,
    };

    public static string? For(string? operationId) => operationId is not null && Bodies.TryGetValue(operationId, out string? body) ? body : null;
}
