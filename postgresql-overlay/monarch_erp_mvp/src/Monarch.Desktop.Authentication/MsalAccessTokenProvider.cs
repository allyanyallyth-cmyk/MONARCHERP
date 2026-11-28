using Microsoft.Identity.Client;
using Monarch.Core.Abstractions;

namespace Monarch.Desktop.Authentication;

/// <summary>
/// MSAL public-client token provider. Uses the system browser and loopback redirect.
/// MSAL's default in-memory cache is used; no token cache is persisted to disk.
/// </summary>
public sealed class MsalAccessTokenProvider : IAccessTokenProvider, IAsyncDisposable
{
    private readonly IPublicClientApplication _application;
    private readonly string[] _scopes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccount? _account;
    private bool _disposed;

    public MsalAccessTokenProvider(MsalDesktopAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _scopes = options.Scopes.ToArray();
        _application = PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority(options.Authority)
            .WithRedirectUri("http://localhost")
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = _account ?? (await _application.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
            if (account is not null)
            {
                try
                {
                    var silent = await _application.AcquireTokenSilent(_scopes, account)
                        .ExecuteAsync(cancellationToken).ConfigureAwait(false);
                    _account = silent.Account;
                    return RequireAccessToken(silent.AccessToken);
                }
                catch (MsalUiRequiredException)
                {
                    // Interactive reauthentication is required; do not fall back to local credentials.
                }
            }

            var interactive = await _application.AcquireTokenInteractive(_scopes)
                .WithUseEmbeddedWebView(false)
                .WithPrompt(Prompt.SelectAccount)
                .ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _account = interactive.Account;
            return RequireAccessToken(interactive.AccessToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = await _application.GetAccountsAsync().ConfigureAwait(false);
            foreach (var account in accounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _application.RemoveAsync(account).ConfigureAwait(false);
            }
            _account = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string RequireAccessToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("The identity provider returned no access token.");
        return token;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Dispose();
            _account = null;
        }
        return ValueTask.CompletedTask;
    }
}
