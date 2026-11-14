using System;

namespace AlUmmahNow.CoreBridge.Passkeys
{
    /// <summary>
    /// What a window shows after a passkey ceremony: nothing (done, or the person closed the sheet),
    /// or one in-app dialog with an i18n message and, for sign-in problems, "Sign in another way".
    /// </summary>
    public sealed class PasskeyOutcome
    {
        public static readonly PasskeyOutcome Done = new PasskeyOutcome(null, false);
        public static readonly PasskeyOutcome Silent = new PasskeyOutcome(null, false);

        private PasskeyOutcome(string? messageKey, bool offerOtherWay)
        {
            MessageKey = messageKey;
            OfferOtherWay = offerOtherWay;
        }

        /// <summary>The i18n key of the dialog text; null shows no dialog.</summary>
        public string? MessageKey { get; }

        /// <summary>The dialog offers "Sign in another way" (the browser sign-in).</summary>
        public bool OfferOtherWay { get; }

        public bool ShowsDialog => MessageKey != null;

        /// <summary>Maps a failed sign-in (<paramref name="signIn"/>) or "Add a passkey".</summary>
        public static PasskeyOutcome ForError(Exception error, bool signIn)
        {
            if (error is OperationCanceledException) return Silent;
            if (!(error is PasskeyException p)) return new PasskeyOutcome(signIn ? "passkey.error.generic" : "passkey.error.addFailed", signIn);
            switch (p.Error)
            {
                case PasskeyError.Cancelled:
                    return Silent;
                case PasskeyError.NoPasskey:
                    return signIn ? new PasskeyOutcome("passkey.error.none", true) : new PasskeyOutcome("passkey.error.addFailed", false);
                case PasskeyError.Rejected:
                    return signIn ? new PasskeyOutcome("passkey.error.rejected", true) : new PasskeyOutcome("passkey.error.addFailed", false);
                case PasskeyError.Unavailable:
                    return new PasskeyOutcome("passkey.error.unsupported", signIn);
                case PasskeyError.AlreadyRegistered:
                    return new PasskeyOutcome("passkey.error.exists", false);
                case PasskeyError.Limit:
                    return new PasskeyOutcome("passkey.error.limit", false);
                case PasskeyError.Offline:
                    return new PasskeyOutcome("passkey.error.offline", false);
                case PasskeyError.RateLimited:
                    return new PasskeyOutcome("passkey.error.rateLimited", false);
                default:
                    return new PasskeyOutcome(signIn ? "passkey.error.generic" : "passkey.error.addFailed", signIn);
            }
        }
    }
}
