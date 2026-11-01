#if __IOS__
using AuthenticationServices;
using Foundation;
using UIKit;

namespace Singulink.Net.Http.Api.Client;

/// <content>
/// The iOS sessions: <see cref="ASWebAuthenticationSession"/>, presented from the window the person last used.
/// </content>
internal static partial class WebAuthenticationNative
{
    // Runs when the type is first used, such as when an authenticator checks whether it is supported, so that the window the person uses is tracked
    // before the first session.
    static WebAuthenticationNative() => PresentationAnchorProvider.StartTracking();

    private static bool IsSupportedCore => true;

    private static async Task<WebAuthenticationNativeResult> AuthenticateCoreAsync(
        Uri requestUri, Uri callbackUri, bool prefersEphemeralWebBrowserSession, CancellationToken cancellationToken)
    {
        // Continuations run asynchronously, so that completing never runs the awaiter on the session's completion handler.
        var completion = new TaskCompletionSource<WebAuthenticationNativeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ASWebAuthenticationSession? session = null;

        // The session is only touched on the main thread, so cancellation, which also runs there, can't miss a session that is about to start.
        RunOnMainThread(() => {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var url = NSUrl.FromString(requestUri.AbsoluteUri) ?? throw new ArgumentException("The request address is not valid.", nameof(requestUri));

                session = CreateSession(url, callbackUri.Scheme, (callbackUrl, error) => {
                    // An exception must not escape into the native completion handler.
                    try
                    {
                        completion.TrySetResult(CreateResult(callbackUrl, error));
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                });

                session.PresentationContextProvider = PresentationAnchorProvider.Instance;
                session.PrefersEphemeralWebBrowserSession = prefersEphemeralWebBrowserSession;

                if (!session.Start())
                    completion.TrySetResult(new(WebAuthenticationNativeStatus.NotOpened, ErrorMessage: "The web authentication session could not be started."));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        // Apple doesn't document that cancelling a session calls its completion handler, so the operation completes here, and the session is closed.
        var registration = cancellationToken.Register(() => {
            completion.TrySetCanceled(cancellationToken);
            RunOnMainThread(() => session?.Cancel());
        });

        try
        {
            var result = await completion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            return result;
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            RunOnMainThread(() => session?.Dispose());
        }
    }

    private static ASWebAuthenticationSession CreateSession(NSUrl url, string callbackScheme, ASWebAuthenticationSessionCompletionHandler completionHandler)
    {
        if (OperatingSystem.IsIOSVersionAtLeast(17, 4))
            return new ASWebAuthenticationSession(url, ASWebAuthenticationSessionCallback.Create(callbackScheme), completionHandler);

#pragma warning disable CA1422 // Obsolete on iOS 17.4, which has the overload above.
        return new ASWebAuthenticationSession(url, callbackScheme, completionHandler);
#pragma warning restore CA1422
    }

    private static WebAuthenticationNativeResult CreateResult(NSUrl? callbackUrl, NSError? error)
    {
        if (error is null)
        {
            return callbackUrl?.AbsoluteString is { } callbackUri ?
                new(WebAuthenticationNativeStatus.Success, callbackUri) :
                new(WebAuthenticationNativeStatus.Error, ErrorMessage: "The web authentication session ended without a callback address.");
        }

        if (error.Domain == ASWebAuthenticationSessionErrorCode.CanceledLogin.GetDomain()?.ToString())
        {
            switch ((ASWebAuthenticationSessionErrorCode)(long)error.Code)
            {
                case ASWebAuthenticationSessionErrorCode.CanceledLogin:
                    return new(WebAuthenticationNativeStatus.UserCancel);
                case ASWebAuthenticationSessionErrorCode.PresentationContextNotProvided:
                case ASWebAuthenticationSessionErrorCode.PresentationContextInvalid:
                    return new(WebAuthenticationNativeStatus.NotOpened, ErrorMessage: $"{error.LocalizedDescription} ({error.Domain} {error.Code})");
            }
        }

        return new(WebAuthenticationNativeStatus.Error, ErrorMessage: $"{error.LocalizedDescription} ({error.Domain} {error.Code})");
    }

    private static void RunOnMainThread(Action action)
    {
        if (NSThread.IsMain)
            action();
        else
            NSThread.MainThread.BeginInvokeOnMainThread(action);
    }

    /// <summary>
    /// Presents sessions from the window that most recently became the key window, which is the one the person last used, as long as it is still the key
    /// window of a scene in the foreground. Otherwise, such as before any window became key while tracking, the visible windows are ranked: the key window
    /// of a foreground scene first, then any window of a foreground scene, then any other window, in the order of their scenes' persistent identifiers.
    /// </summary>
    /// <remarks>
    /// Tracking covers apps with several scenes in the foreground at once, such as two windows side by side on iPad: each scene has a key window of its
    /// own, and the ranking alone would pick one of them by its scene's identifier rather than the one the person is using.
    /// </remarks>
    private sealed class PresentationAnchorProvider : NSObject, IASWebAuthenticationPresentationContextProviding
    {
        private static NSObject? _observer;
        private static WeakReference<UIWindow>? _lastKeyWindow;

        public static PresentationAnchorProvider Instance { get; } = new();

        /// <summary>
        /// Starts tracking the window that most recently became the key window. Called once, by the static constructor.
        /// </summary>
        public static void StartTracking()
        {
            _observer = NSNotificationCenter.DefaultCenter.AddObserver(UIWindow.DidBecomeKeyNotification, notification => {
                if (notification.Object is not UIWindow window)
                    return;

                if (_lastKeyWindow is null)
                    _lastKeyWindow = new(window);
                else
                    _lastKeyWindow.SetTarget(window);
            });
        }

        public UIWindow GetPresentationAnchor(ASWebAuthenticationSession session)
        {
            if (_lastKeyWindow is not null && _lastKeyWindow.TryGetTarget(out var lastKeyWindow) && lastKeyWindow.IsKeyWindow && !lastKeyWindow.Hidden &&
                lastKeyWindow.WindowScene?.ActivationState is UISceneActivationState.ForegroundActive)
            {
                return lastKeyWindow;
            }

            var candidates = new List<(int Rank, string SceneId, int Index, UIWindow Window)>();
            UIWindowScene? anyScene = null;

            foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
            {
                if (scene is not UIWindowScene windowScene)
                    continue;

                bool isActive = windowScene.ActivationState is UISceneActivationState.ForegroundActive;
                string sceneId = windowScene.Session.PersistentIdentifier;

                if (anyScene is null || string.CompareOrdinal(sceneId, anyScene.Session.PersistentIdentifier) < 0)
                    anyScene = windowScene;

                var windows = windowScene.Windows;

                for (int i = 0; i < windows.Length; i++)
                {
                    if (windows[i].Hidden)
                        continue;

                    int rank = isActive ? (windows[i].IsKeyWindow ? 0 : 1) : 2;
                    candidates.Add((rank, sceneId, i, windows[i]));
                }
            }

            if (candidates.Count > 0)
            {
                return candidates
                    .OrderBy(c => c.Rank)
                    .ThenBy(c => c.SceneId, StringComparer.Ordinal)
                    .ThenBy(c => c.Index)
                    .First().Window;
            }

            // Only an app that shows no window gets here, which can't have started the session.
            if (anyScene is not null)
                return new UIWindow(anyScene);

#pragma warning disable CA1422 // Obsolete on iOS 26, but apps without a scene have no other way.
            return new UIWindow();
#pragma warning restore CA1422
        }
    }
}
#endif
