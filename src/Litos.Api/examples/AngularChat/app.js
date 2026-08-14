'use strict';

/*
 * Litos Chat example (AngularJS) — a genuinely external, buildless client for Litos.Api, in the
 * same spirit as ../BlazorChat but with no server-side component at all: this page calls
 * Litos.Api directly from the browser (see README.md's CORS_ALLOWED_ORIGINS requirement) and
 * keeps the signed-in user's JWT pair in sessionStorage rather than a server-issued cookie.
 *
 * Mirrors BlazorChat's LitosClient/* and Chat.razor closely enough that the two are worth reading
 * side by side; comments here focus on where this port necessarily differs (token storage, SSE
 * parsing over fetch's ReadableStream instead of a Stream, no server-side circuit).
 */
angular.module('litosChat', [])

// Wraps POST /auth/token and /auth/token/refresh. A separate, tokenless $http call — mirrors
// BlazorChat's LitosLoginService being on its own unauthenticated HttpClient (see that file's
// doc comment: wiring token-refresh onto the calls that mint/refresh tokens would be circular).
.factory('authService', ['$http', '$window', 'apiBaseUrl', function ($http, $window, apiBaseUrl) {
    var STORAGE_KEY = 'litosChat.session';

    function formEncode(fields) {
        return Object.keys(fields).map(function (key) {
            return encodeURIComponent(key) + '=' + encodeURIComponent(fields[key]);
        }).join('&');
    }

    function loadSession() {
        var raw = $window.sessionStorage.getItem(STORAGE_KEY);
        return raw ? angular.fromJson(raw) : null;
    }

    function saveSession(session) {
        $window.sessionStorage.setItem(STORAGE_KEY, angular.toJson(session));
    }

    function clearSession() {
        $window.sessionStorage.removeItem(STORAGE_KEY);
    }

    return {
        // Current session (or null) — re-read from sessionStorage on every call rather than
        // cached in a JS variable, so a second tab's login/logout is picked up on next use.
        current: loadSession,

        login: function (username, password) {
            return $http.post(apiBaseUrl + '/auth/token', formEncode({ username: username, password: password }), {
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            }).then(function (response) {
                var session = {
                    username: username,
                    accessToken: response.data.accessToken,
                    refreshToken: response.data.refreshToken,
                };
                saveSession(session);
                return session;
            });
        },

        // Returns the new session on success, or null on failure (refresh token itself expired/
        // revoked) — same "leave it to the caller" contract as LitosLoginService.RefreshAsync.
        refresh: function () {
            var session = loadSession();
            if (!session) return $q_reject();

            return $http.post(apiBaseUrl + '/auth/token/refresh', formEncode({ refreshToken: session.refreshToken }), {
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            }).then(function (response) {
                var updated = angular.extend({}, session, {
                    accessToken: response.data.accessToken,
                    refreshToken: response.data.refreshToken,
                });
                saveSession(updated);
                return updated;
            }, function () {
                clearSession();
                return null;
            });
        },

        logout: clearSession,
    };

    function $q_reject() {
        // authService has no $q dependency of its own beyond this one spot; inlined rather than
        // adding $q just for a single immediately-rejected promise.
        return { then: function (_ok, err) { return err && err(); } };
    }
}])

// Thin wrapper over $http + the hand-rolled SSE reader — mirrors LitosApiClient.cs. Deliberately
// bypasses Angular's $http for the streaming turn POST: $http buffers the full response body
// before resolving, which defeats streaming entirely, so SendTurn uses the Fetch API directly
// (same reason BlazorChat's LitosApiClient reads the HttpResponseMessage's stream itself rather
// than awaiting a buffered body).
.factory('litosApiClient', ['$http', '$q', 'authService', 'apiBaseUrl', function ($http, $q, authService, apiBaseUrl) {
    var MAX_FILE_BYTES = 20 * 1024 * 1024; // Mirrors TurnsEndpoints.MaxAttachmentBytes.
    var MAX_FILE_COUNT = 5; // Mirrors TurnsEndpoints.MaxAttachmentCount.

    function authHeader() {
        var session = authService.current();
        return session ? 'Bearer ' + session.accessToken : undefined;
    }

    // Runs `perform` (a function returning a fetch Promise) with the current access token: on a
    // 401, attempts one silent refresh and retries once, mirroring JwtRefreshHandler.SendAsync. A
    // second 401 propagates to the caller (Chat.razor-equivalent's catch), which signs the user
    // out to /login — same contract as the .NET example.
    function withAuthRetry(perform) {
        return perform(authHeader()).then(function (response) {
            if (response.status !== 401) return response;

            return authService.refresh().then(function (refreshed) {
                if (!refreshed) return response;
                return perform('Bearer ' + refreshed.accessToken);
            });
        });
    }

    function checkOk(response) {
        if (!response.ok) {
            return response.text().then(function (body) {
                return $q.reject(new Error('Litos.Api returned ' + response.status + (body ? ': ' + body : '')));
            });
        }
        return response;
    }

    return {
        maxFileBytes: MAX_FILE_BYTES,
        maxFileCount: MAX_FILE_COUNT,

        listSessions: function () {
            return $http.get(apiBaseUrl + '/sessions', { headers: { Authorization: authHeader() } })
                .then(function (r) { return r.data; });
        },

        getHistory: function (sessionId) {
            return $http.get(apiBaseUrl + '/sessions/' + encodeURIComponent(sessionId) + '/history', {
                headers: { Authorization: authHeader() },
            }).then(function (r) { return r.data; });
        },

        // Resolves to { kind: 'started', events: <async generator> } | { kind: 'steered'|'queued', message }
        // — mirrors TurnOutcome.cs's three-way split (TurnsEndpoints.StartOrSteer).
        sendTurn: function (sessionId, text, attachments) {
            var url = apiBaseUrl + '/sessions/' + encodeURIComponent(sessionId) + '/turns';

            function buildBody() {
                if (attachments.length === 0) {
                    return { body: angular.toJson({ input: text }), contentType: 'application/json' };
                }
                var form = new FormData();
                if (text) form.append('input', text);
                attachments.forEach(function (a) {
                    form.append('file', a.blob, a.fileName);
                });
                return { body: form, contentType: null }; // fetch sets the multipart boundary itself.
            }

            var built = buildBody();

            return withAuthRetry(function (bearer) {
                var headers = { Authorization: bearer };
                if (built.contentType) headers['Content-Type'] = built.contentType;
                return fetch(url, { method: 'POST', headers: headers, body: built.body });
            }).then(checkOk).then(function (response) {
                if (response.status === 202) {
                    return response.text().then(function (message) {
                        // TurnsEndpoints distinguishes Steered from Queued only by response
                        // wording — same match BlazorChat's LitosApiClient.cs uses.
                        var kind = /queued/i.test(message) ? 'queued' : 'steered';
                        return { kind: kind, message: message };
                    });
                }
                return { kind: 'started', events: readAgentEvents(response) };
            });
        },
    };

    // Minimal Server-Sent Events reader over a fetch Response's ReadableStream — the browser
    // equivalent of AgentEventStreamParser.cs (there's no native client-side SSE parser that
    // works with fetch either, and EventSource can't do POST/custom headers, hence hand-rolling
    // this exactly like the .NET example does). Returns an async generator of parsed event
    // objects; events are separated by a blank line, only "data:" lines are read.
    function readAgentEvents(response) {
        var reader = response.body.getReader();
        var decoder = new TextDecoder('utf-8');

        return {
            [Symbol.asyncIterator]: function () {
                var buffer = '';
                var dataLines = [];
                var done = false;

                function flushEvent() {
                    if (dataLines.length === 0) return null;
                    var evt = parseAgentEvent(dataLines.join('\n'));
                    dataLines = [];
                    return evt;
                }

                return {
                    next: function () {
                        return pump();

                        function pump() {
                            // Drain any complete lines already buffered before reading more bytes.
                            var newlineIndex = buffer.indexOf('\n');
                            if (newlineIndex >= 0) {
                                var line = buffer.slice(0, newlineIndex).replace(/\r$/, '');
                                buffer = buffer.slice(newlineIndex + 1);

                                if (line.length === 0) {
                                    var evt = flushEvent();
                                    if (evt) return $q.resolve({ value: evt, done: false });
                                    return pump();
                                }
                                if (line.indexOf('data:') === 0) {
                                    dataLines.push(line.slice(5).replace(/^\s/, ''));
                                }
                                return pump();
                            }

                            if (done) {
                                var finalEvt = flushEvent();
                                if (finalEvt) return $q.resolve({ value: finalEvt, done: false });
                                return $q.resolve({ value: undefined, done: true });
                            }

                            return reader.read().then(function (result) {
                                if (result.done) {
                                    done = true;
                                    buffer += decoder.decode();
                                } else {
                                    buffer += decoder.decode(result.value, { stream: true });
                                }
                                return pump();
                            });
                        }
                    },
                };
            },
        };
    }

    // Local mirror of Litos.Agent's AgentEvent wire shape — same field-presence classification as
    // AgentEventDto.Parse (the JSON carries no type discriminator). Kept in this file rather than
    // split out, since this is the only consumer and the example intentionally has no build step
    // to justify a module split.
    function parseAgentEvent(json) {
        var obj;
        try {
            obj = angular.fromJson(json);
        } catch (e) {
            return { type: 'unknown', raw: json };
        }

        if (typeof obj.Text === 'string') {
            // TextDelta and ReasoningDelta share the identical {"Text": "..."} shape; like
            // BlazorChat's AgentEventDto.Parse, this example can't tell them apart and renders
            // both as ordinary assistant text.
            return { type: 'textDelta', text: obj.Text };
        }
        if (obj.Result !== undefined) {
            var result = obj.Result || {};
            return {
                type: 'toolCallResult',
                callId: obj.CallId || '',
                toolName: obj.ToolName || '',
                success: !result.IsError,
                resultText: typeof result.Text === 'string' ? result.Text : angular.toJson(result),
            };
        }
        if (obj.Arguments !== undefined) {
            return { type: 'toolCallCompleted', callId: obj.CallId || '', toolName: obj.ToolName || '' };
        }
        if (obj.Reason !== undefined && obj.CallId !== undefined) {
            return { type: 'toolCallSkipped', callId: obj.CallId, reason: obj.Reason };
        }
        if (obj.ToolName !== undefined && obj.CallId !== undefined) {
            return { type: 'toolCallStarted', callId: obj.CallId, toolName: obj.ToolName };
        }
        if (obj.Message !== undefined && obj.Usage !== undefined) {
            return { type: 'messageCompleted' };
        }
        if (obj.Exception !== undefined) {
            return { type: 'error', message: (obj.Exception && obj.Exception.Message) || 'An error occurred.' };
        }
        if (obj.TokensBefore !== undefined) {
            return { type: 'compaction' };
        }
        return { type: 'unknown', raw: json };
    }
}])

.controller('ChatController', ['$scope', '$q', '$timeout', 'authService', 'litosApiClient', function ($scope, $q, $timeout, authService, litosApiClient) {
    $scope.session = authService.current();

    $scope.loginForm = { username: '', password: '' };
    $scope.loginError = null;
    $scope.loggingIn = false;

    $scope.login = function () {
        $scope.loginError = null;
        $scope.loggingIn = true;
        authService.login($scope.loginForm.username, $scope.loginForm.password).then(function (session) {
            $scope.session = session;
            $scope.loggingIn = false;
            initChat();
        }, function () {
            $scope.loginError = 'Invalid username or password, or Litos.Api is unreachable.';
            $scope.loggingIn = false;
        });
    };

    $scope.logout = function () {
        authService.logout();
        $scope.session = null;
    };

    // --- Chat state (mirrors Chat.razor's @code block) ---

    $scope.sessionId = null;
    $scope.sessions = [];
    // Plain-primitive ng-model bindings (sessionSearch, draftText) break under the session-bar/
    // chat-layout ng-if="session" toggle: ng-if creates a new child scope on that transition, and
    // ng-model on a bare primitive creates a shadowing own-property on the child rather than
    // writing through to this (root) scope — so functions defined here that read $scope.draftText
    // directly (canSend, send) would keep seeing the stale root-scope value forever, while the
    // DOM's own scope shows the child's copy. Nesting both under an object sidesteps this: ng-model
    // then writes through the shared object reference instead of shadowing a primitive.
    $scope.search = { text: '' };
    $scope.entries = [];
    $scope.pendingAttachments = [];
    $scope.composer = { text: '' };
    $scope.isSending = false;
    $scope.error = null;

    var toolActivityByCallId = {};

    function newSessionId() {
        // RFC4122-ish random hex id — matches BlazorChat's Guid.NewGuid("n") shape closely enough
        // (Litos.Api treats the URL segment as an opaque string, so exact GUID formatting doesn't
        // matter, just unpredictability/uniqueness per conversation).
        var bytes = new Uint8Array(16);
        (window.crypto || window.msCrypto).getRandomValues(bytes);
        return Array.prototype.map.call(bytes, function (b) { return b.toString(16).padStart(2, '0'); }).join('');
    }

    function refreshSessions() {
        return litosApiClient.listSessions().then(function (sessions) {
            $scope.sessions = sessions;
        }, function (err) {
            $scope.error = 'Could not load conversations: ' + err.message;
        });
    }

    $scope.filteredSessions = function () {
        var query = ($scope.search.text || '').toLowerCase();
        return $scope.sessions.filter(function (s) {
            return !query || (s.firstUserMessagePreview || '').toLowerCase().indexOf(query) >= 0;
        }).sort(function (a, b) {
            return new Date(b.lastUpdatedAt) - new Date(a.lastUpdatedAt);
        });
    };

    function resetComposer() {
        $scope.entries = [];
        toolActivityByCallId = {};
        $scope.pendingAttachments = [];
        $scope.composer.text = '';
        $scope.error = null;
    }

    $scope.startNewSession = function () {
        if ($scope.isSending) return;
        $scope.sessionId = newSessionId();
        resetComposer();
        refreshSessions();
    };

    $scope.selectSession = function (sessionId) {
        if ($scope.isSending || sessionId === $scope.sessionId) return;
        $scope.sessionId = sessionId;
        resetComposer();

        litosApiClient.getHistory(sessionId).then(function (history) {
            history.forEach(function (message) {
                if (message.role === 'user') {
                    $scope.entries.push({
                        kind: 'user',
                        text: message.text,
                        attachmentNames: message.attachments > 0 ? [message.attachments + ' attachment(s)'] : [],
                    });
                } else if (message.role === 'assistant' && message.text && message.text.trim()) {
                    $scope.entries.push({ kind: 'assistant', text: message.text });
                }
            });
            scrollToBottom();
        }, function (err) {
            $scope.error = 'Could not load conversation: ' + err.message;
        });
    };

    function initChat() {
        $scope.sessionId = newSessionId();
        refreshSessions();
    }

    // --- Attachments ---

    $scope.onFilesSelected = function (files) {
        $scope.error = null;
        var remaining = litosApiClient.maxFileCount - $scope.pendingAttachments.length;
        if (remaining <= 0) {
            $scope.error = 'You can attach up to ' + litosApiClient.maxFileCount + ' files per message.';
            return;
        }

        Array.prototype.slice.call(files, 0, remaining).forEach(function (file) {
            if (file.size > litosApiClient.maxFileBytes) {
                $scope.error = "'" + file.name + "' is larger than the " +
                    (litosApiClient.maxFileBytes / (1024 * 1024)) + 'MB limit and was not added.';
                return;
            }
            $scope.pendingAttachments.push({ fileName: file.name, contentType: file.type, blob: file });
        });
        $timeout(angular.noop); // File input's change fires outside Angular's digest.
    };

    $scope.removeAttachment = function (index) {
        $scope.pendingAttachments.splice(index, 1);
    };

    $scope.formatSize = function (bytes) {
        return bytes < 1024 * 1024
            ? Math.max(1, Math.round(bytes / 1024)) + ' KB'
            : (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    };

    // --- Sending / streaming ---

    $scope.onComposerKeyDown = function (event) {
        // Enter sends; Shift+Enter keeps the natural textarea newline behavior.
        if (event.key === 'Enter' && !event.shiftKey && !$scope.isSending) {
            event.preventDefault();
            $scope.send();
        }
    };

    $scope.canSend = function () {
        return !$scope.isSending && ($scope.composer.text.trim() || $scope.pendingAttachments.length > 0);
    };

    $scope.send = function () {
        if (!$scope.canSend()) return;

        var text = $scope.composer.text;
        var attachments = $scope.pendingAttachments;
        $scope.entries.push({
            kind: 'user',
            text: text,
            attachmentNames: attachments.map(function (a) { return a.fileName; }),
        });
        $scope.composer.text = '';
        $scope.pendingAttachments = [];
        $scope.isSending = true;
        $scope.error = null;
        scrollToBottom();

        litosApiClient.sendTurn($scope.sessionId, text, attachments).then(function (outcome) {
            if (outcome.kind === 'started') {
                return consumeStream(outcome.events);
            }
            $scope.entries.push({ kind: 'system', text: outcome.message });
        }).catch(function (err) {
            $scope.error = 'Request to Litos.Api failed: ' + err.message;
        }).finally(function () {
            $scope.isSending = false;
            scrollToBottom();
            refreshSessions();
            $scope.$applyAsync();
        });
    };

    function consumeStream(events) {
        var currentMessage = null;
        var iterator = events[Symbol.asyncIterator]();

        return iterate();

        function iterate() {
            return iterator.next().then(function (result) {
                if (result.done) return;

                var evt = result.value;
                switch (evt.type) {
                    case 'textDelta':
                        if (!currentMessage) {
                            currentMessage = { kind: 'assistant', text: '' };
                            $scope.entries.push(currentMessage);
                        }
                        currentMessage.text += evt.text;
                        break;

                    case 'toolCallStarted': {
                        var activity = { kind: 'tool', toolName: evt.toolName, status: 'running', detail: null };
                        toolActivityByCallId[evt.callId] = activity;
                        $scope.entries.push(activity);
                        // A tool result may still stream more assistant text afterward in the same
                        // turn — closing currentMessage here starts a fresh bubble for it, rather
                        // than appending after an interleaved tool-activity entry.
                        currentMessage = null;
                        break;
                    }

                    case 'toolCallResult':
                        replaceToolActivity(evt.callId, evt.success ? 'succeeded' : 'failed', truncate(evt.resultText));
                        break;

                    case 'toolCallSkipped':
                        replaceToolActivity(evt.callId, 'skipped', evt.reason);
                        break;

                    case 'error':
                        $scope.entries.push({ kind: 'system', text: 'Error: ' + evt.message });
                        break;

                    case 'compaction':
                        $scope.entries.push({ kind: 'system', text: '(conversation history was compacted to stay within context limits)' });
                        break;

                    case 'messageCompleted':
                        currentMessage = null;
                        break;
                }

                scrollToBottom();
                $scope.$applyAsync();
                return iterate();
            });
        }
    }

    function replaceToolActivity(callId, status, detail) {
        var activity = toolActivityByCallId[callId];
        if (!activity) return;
        activity.status = status;
        activity.detail = detail;
    }

    function truncate(text, maxLength) {
        maxLength = maxLength || 500;
        return text.length <= maxLength ? text : text.slice(0, maxLength) + '…';
    }

    function scrollToBottom() {
        $timeout(function () {
            var el = document.getElementById('transcript');
            if (el) el.scrollTo({ top: el.scrollHeight, behavior: 'smooth' });
        }, 0);
    }

    if ($scope.session) {
        initChat();
    }
}])

.constant('apiBaseUrl', (function () {
    // ?api=... always wins, for pointing this page at a non-default Litos.Api origin. Otherwise:
    // if this page is being served from Litos.Api's own wwwroot, same-origin just works. If not
    // (e.g. `npx serve` on its own port, as in this example's README), default to Litos.Api's
    // standard local port rather than window.location.origin, which would otherwise resolve to
    // this static server itself and 404 every request. Edit this fallback if your container
    // listens somewhere else.
    var params = new URLSearchParams(window.location.search);
    return params.get('api') || 'http://localhost:8080';
})());
