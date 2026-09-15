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

        // "Cancel".
        ["CancelOrder"] = """
            {
              "reason": "customer_request"
            }
            """,
    };

    public static string? For(string? operationId) => operationId is not null && Bodies.TryGetValue(operationId, out string? body) ? body : null;
}
