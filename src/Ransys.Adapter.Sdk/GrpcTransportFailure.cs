using System.Net.Sockets;
using Grpc.Core;

namespace Ransys.Adapter.Sdk;

/// <summary>
/// Decides whether a failed gRPC call is <b>proven</b> not to have been sent (Provider Adapter Contract v1 §10, §13;
/// ADR-005). Anything that is not proven is treated as possibly sent.
/// </summary>
internal static class GrpcTransportFailure
{
    /// <summary>
    /// True only when the call failed while establishing the connection, before any request bytes could be
    /// written: <c>Unavailable</c> caused by a name resolution failure, a refused/unreachable connect, or an
    /// <see cref="HttpRequestException"/> whose <see cref="HttpRequestError"/> is a connection/name resolution
    /// error (raised by SocketsHttpHandler only while connecting). Resets, protocol errors, deadlines and unknown
    /// causes return false.
    /// </summary>
    public static bool ProvesNotSent(RpcException exception)
    {
        if (exception.StatusCode != StatusCode.Unavailable)
        {
            return false;
        }

        for (var cause = exception.Status.DebugException; cause is not null; cause = cause.InnerException)
        {
            switch (cause)
            {
                case HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }:
                    return true;
                case HttpRequestException:
                    // Any other HTTP failure (reset, protocol error, response ended, unknown) may follow a send.
                    return false;
                case SocketException socket when socket.SocketErrorCode is SocketError.ConnectionRefused
                    or SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable:
                    return true;
            }
        }

        return false;
    }
}
