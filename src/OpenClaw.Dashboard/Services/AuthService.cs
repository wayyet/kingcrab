using System.Net.Http.Json;
using Microsoft.JSInterop;
using OpenClaw.Dashboard.Models;

namespace OpenClaw.Dashboard.Services;

public class AuthService
{
    private readonly ApiService _api;
    private readonly IJSRuntime _js;

    public AuthState? CurrentAuth { get; private set; }

    public bool IsAuthenticated => CurrentAuth != null;

    public event Action? OnAuthStateChanged;

    public AuthService(ApiService api, IJSRuntime js)
    {
        _api = api;
        _js = js;
    }

    /// <summary>Check if the server already knows us (via cookie or existing session).</summary>
    public async Task SyncAuth()
    {
        try
        {
            var state = await _api.GetAsync<AuthState>("auth/session").ConfigureAwait(false);
            SetAuth(state);
        }
        catch
        {
            SetAuth(null);
        }
    }

    /// <summary>
    /// Try to authenticate using an OIDC/Keycloak token obtained from the browser.
    /// Call this after the JS callback has stored a token.
    /// </summary>
    public async Task<bool> LoginWithOidc()
    {
        try
        {
            // Get the OIDC access token from JS
            var token = await _js.InvokeAsync<string?>("DashboardAuth.getAccessToken").ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                // Maybe the token expired — try refreshing
                token = await _js.InvokeAsync<string?>("DashboardAuth.refreshToken").ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            // Set the Bearer token in ApiService so the request uses it
            _api.SetBearerToken(token);

            // Try to authenticate with the server using this token
            var success = await PostLoginAsync(new { mode = "oidc_jwt", token }).ConfigureAwait(false);
            if (success)
            {
                return true;
            }

            // If server rejected the token, clear it locally
            _api.SetBearerToken(null);
            await _js.InvokeVoidAsync("DashboardAuth.clearLocalToken").ConfigureAwait(false);
            return false;
        }
        catch
        {
            _api.SetBearerToken(null);
            return false;
        }
    }

    /// <summary>Initiate OIDC login — redirects the browser to the Keycloak login page.</summary>
    public async Task InitiateOidcLogin()
    {
        await _js.InvokeVoidAsync("DashboardAuth.login").ConfigureAwait(false);
    }

    /// <summary>Check if the page was loaded after an OIDC callback.</summary>
    public async Task<bool> CheckOidcCallback()
    {
        try
        {
            var handled = await _js.InvokeAsync<bool>("DashboardAuth.wasCallbackHandled")
                .ConfigureAwait(false);
            return handled;
        }
        catch
        {
            return false;
        }
    }

    public Task<bool> LoginWithCredentials(string username, string password)
        => PostLoginAsync(new { mode = "credentials", username, password });

    public Task<bool> LoginWithToken(string token)
        => PostLoginAsync(new { mode = "token", token });

    public Task<bool> LoginWithBootstrap(string bootstrapToken)
        => PostLoginAsync(new { mode = "bootstrap", bootstrapToken });

    private async Task<bool> PostLoginAsync(object body)
    {
        try
        {
            using var response = await _api.PostRawAsync("auth/session", body).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                SetAuth(null);
                return false;
            }

            AuthState? state = null;
            if (response.Content.Headers.ContentLength != 0)
            {
                try
                {
                    state = await response.Content
                        .ReadFromJsonAsync<AuthState>()
                        .ConfigureAwait(false);
                }
                catch
                {
                    state = null;
                }
            }

            if (state is null)
            {
                await SyncAuth().ConfigureAwait(false);
                return IsAuthenticated;
            }

            SetAuth(state);
            return true;
        }
        catch
        {
            SetAuth(null);
            return false;
        }
    }

    public async Task Logout()
    {
        // If OIDC, redirect to the Keycloak logout endpoint
        if (IsAuthenticated && CurrentAuth?.AuthMode == "oidc_jwt")
        {
            try
            {
                await _js.InvokeVoidAsync("DashboardAuth.logout").ConfigureAwait(false);
                // logout() redirects the browser — we won't normally reach here,
                // but if the Keycloak session endpoint is unreachable, fall through
                return;
            }
            catch
            {
                // Fall through to normal logout
            }
        }

        try
        {
            using var _ = await _api.DeleteAsync("auth/session").ConfigureAwait(false);
        }
        catch
        {
            // swallow — we still clear local state
        }

        _api.SetBearerToken(null);

        try
        {
            await _js.InvokeVoidAsync("DashboardAuth.clearLocalToken").ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        SetAuth(null);
    }

    public bool HasRole(string requiredRole)
    {
        if (CurrentAuth is null)
        {
            return false;
        }

        return RoleRank(CurrentAuth.Role) >= RoleRank(requiredRole);
    }

    private static int RoleRank(string? role)
    {
        return role?.ToLowerInvariant() switch
        {
            "admin" => 3,
            "operator" => 2,
            "viewer" => 1,
            _ => 0
        };
    }

    public async Task<string?> GetOperatorToken()
    {
        try
        {
            using var response = await _api.GetRawAsync("auth/operator-token").ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OperatorTokenResponse>()
                .ConfigureAwait(false);
            return payload?.Token;
        }
        catch
        {
            return null;
        }
    }

    private void SetAuth(AuthState? state)
    {
        var changed = !Equals(CurrentAuth, state);
        CurrentAuth = state;
        _api.SetCsrfToken(state?.CsrfToken);
        if (changed)
        {
            OnAuthStateChanged?.Invoke();
        }
    }

    private sealed record OperatorTokenResponse(string? Token);
}
