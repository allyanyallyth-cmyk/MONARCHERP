using System.Threading;
using System.Threading.Tasks;

namespace Monarch.Core.Abstractions;

/// <summary>
/// Supplies a current OIDC access token. Implementations must acquire/refresh tokens
/// using the configured public-client flow and must never return an ID token.
/// </summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}
