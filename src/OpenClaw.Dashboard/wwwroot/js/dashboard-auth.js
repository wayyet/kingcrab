/**
 * OpenClaw Dashboard — OIDC / Keycloak Auth Bridge
 *
 * Thin wrapper around the shared oidc-auth.js library.
 * Exposes JSInvokable functions that the Blazor AuthService calls via IJSRuntime.
 *
 * Defaults (matching Gateway's appsettings.json Security:Oidc section):
 *   Authority: https://passport.ai4c.cn/realms/ai4c-saas
 *   Client ID: ncrew-client
 */
(function () {
    'use strict';

    var OIDC_DEFAULT_AUTHORITY = 'https://passport.ai4c.cn/realms/ai4c-saas';
    var OIDC_DEFAULT_CLIENT_ID  = 'ncrew-client';
    var OIDC_CONFIG_KEY = 'openclaw_dashboard_oidc_config';
    var TOKEN_KEY = 'openclaw_dashboard_oidc_token';

    var _client = null;
    var _callbackHandled = false;

    // ── Helpers ──────────────────────────────────────────────────────────────────

    function loadConfig() {
        try {
            var raw = localStorage.getItem(OIDC_CONFIG_KEY);
            if (raw) {
                return JSON.parse(raw);
            }
        } catch (_) { /* ignore */ }
        return { authority: OIDC_DEFAULT_AUTHORITY, clientId: OIDC_DEFAULT_CLIENT_ID };
    }

    function saveConfig(config) {
        try {
            localStorage.setItem(OIDC_CONFIG_KEY, JSON.stringify(config));
        } catch (_) { /* ignore */ }
    }

    function getClient() {
        if (!_client) {
            var config = loadConfig();
            _client = window.OpenClawOidc.create({
                authority: config.authority,
                clientId: config.clientId,
                scope: 'openid profile email',
                persist: false  // sessionStorage only
            });
        }
        return _client;
    }

    // ── Page-load callback handling ──────────────────────────────────────────────
    // Run BEFORE Blazor initialises so the URL is cleaned up early.

    (function handleCallbackOnLoad() {
        var params = new URLSearchParams(window.location.search);
        if (params.has('code') || params.has('error')) {
            // Create a temporary client for the callback, then let Blazor re-create it.
            var cfg = loadConfig();
            var tempClient = window.OpenClawOidc.create({
                authority: cfg.authority,
                clientId: cfg.clientId,
                scope: 'openid profile email',
                persist: false
            });
            tempClient.handleRedirectCallback().then(function (result) {
                _callbackHandled = true;
                if (result.ok) {
                    // Signal to Blazor that login was successful by storing a marker
                    sessionStorage.setItem(TOKEN_KEY, 'true');
                }
                // Reload Blazor — it will pick up the token on init
                window.location.reload();
            }).catch(function (err) {
                console.error('[Dashboard Auth] OIDC callback error:', err);
                _callbackHandled = true;
            });
        }
    })();

    // ── Blazor JSInterop API ─────────────────────────────────────────────────────

    window.DashboardAuth = {

        /** Returns the current OIDC access token, or null. */
        getAccessToken: function () {
            try {
                return getClient().getAccessToken();
            } catch (_) {
                return null;
            }
        },

        /** Refresh the token if needed, returning the (possibly refreshed) access token. */
        refreshToken: function () {
            return getClient().refreshIfNeeded(60).then(function (token) {
                return token || null;
            }).catch(function () {
                return null;
            });
        },

        /** Initiate OIDC login — redirects the browser to Keycloak. */
        login: function () {
            return getClient().login();
        },

        /** Initiate OIDC logout — redirects to the Keycloak end-session endpoint. */
        logout: function () {
            return getClient().logout();
        },

        /** Clear local OIDC tokens without server redirect. */
        clearLocalToken: function () {
            try {
                getClient().clear();
                sessionStorage.removeItem(TOKEN_KEY);
            } catch (_) { /* ignore */ }
        },

        /** Returns the saved OIDC config. */
        getConfig: function () {
            return loadConfig();
        },

        /** Save OIDC config (authority + clientId). Returns the config object. */
        saveConfig: function (authority, clientId) {
            var config = {
                authority: authority || OIDC_DEFAULT_AUTHORITY,
                clientId: clientId || OIDC_DEFAULT_CLIENT_ID
            };
            saveConfig(config);
            // Reset client so next getClient() picks up the new config
            _client = null;
            return config;
        },

        /** Returns the defaults */
        getDefaults: function () {
            return {
                authority: OIDC_DEFAULT_AUTHORITY,
                clientId: OIDC_DEFAULT_CLIENT_ID
            };
        },

        /** Check if the callback was already handled on page load. */
        wasCallbackHandled: function () {
            return _callbackHandled;
        }
    };

})();