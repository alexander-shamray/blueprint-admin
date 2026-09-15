using System.Text.Json;

namespace Admin.Host.Broker;

/// <summary>
/// <c>rabbitmqctl ... --formatter json</c> prints one array over several lines, every row after the
/// first led by a comma (measured on rabbitmq:4.1-management-alpine, the base of the backend's
/// deploy/compose/rabbitmq/Dockerfile). Anything printed before the line that opens it is not part of it.
/// </summary>
public static class RabbitCtlJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static IReadOnlyList<T> Parse<T>(IReadOnlyList<string> stdoutLines)
    {
        int start = -1;

        for (int i = 0; i < stdoutLines.Count; i++)
        {
            if (stdoutLines[i].TrimStart().StartsWith('['))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            throw new JsonException("no JSON array in the output");
        }

        List<T> rows = JsonSerializer.Deserialize<List<T>>(string.Join('\n', stdoutLines.Skip(start)), Options)
            ?? throw new JsonException("the output was null");

        foreach (T row in rows)
        {
            if (row is null)
            {
                throw new JsonException("a row was null");
            }
        }

        return rows;
    }
}
