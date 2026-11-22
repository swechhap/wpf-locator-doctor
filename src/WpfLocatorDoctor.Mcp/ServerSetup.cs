using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpfLocatorDoctor.Core.Assistant;
#if LIVE_TOOLS
using WpfLocatorDoctor.Live;
#endif

namespace WpfLocatorDoctor.Mcp;

/// <summary>Registers the tools and decides how answers and errors look to the assistant.</summary>
internal static class ServerSetup
{
    /// <summary>
    /// Answers are JSON. Quotes and angle brackets are written as they are (not as " or <) so XAML snippets stay
    /// readable, and empty fields are left out to keep answers short.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(McpJsonUtilities.DefaultOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IMcpServerBuilder AddWpfLocatorDoctor(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithTools<SourceTools>(JsonOptions);
#if LIVE_TOOLS
        // One live session (one app) for the server's lifetime. The host disposes it on shutdown, closing an app the server started.
        builder.Services.AddSingleton(_ => new LiveSession(new FlaUiAppConnector(), LiveSessionOptions.FromEnvironment()));
        builder.WithTools<LiveTools>(JsonOptions);
#endif
        return builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsUserFacing(ex))
            {
                return ErrorResult(ex.Message);
            }
        }));
    }

    /// <summary>
    /// Mistakes in the request (wrong path, unknown screen…) and files that can't be read. The assistant gets the plain message
    /// and can correct itself; they aren't crashes, so they aren't logged as such.
    /// </summary>
    public static bool IsUserFacing(Exception ex) =>
        ex is DoctorInputException or IOException or UnauthorizedAccessException or McpException;

    public static CallToolResult ErrorResult(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
