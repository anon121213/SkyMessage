using Android;
using Android.App;
using Android.Content;
using Android.Gms.Auth.Api.Identity;
using Android.Gms.Common;
using Android.Gms.Extensions;
using Android.Telephony;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Droid;

/// <summary>
/// The device's own number(s). First the Google Phone Number Hint API (no permission; the system shows a
/// chooser, which also covers dual-SIM phones, and returns E.164). Without Google Play services, or when
/// the hint is unavailable, SubscriptionManager/TelephonyManager with READ_PHONE_NUMBERS (the OS asks the
/// user). Numbers stored on the SIM are often missing — then the user types the number in.
/// </summary>
public sealed class AndroidPhoneNumberService(IPermissionService permissions, ILogger<AndroidPhoneNumberService> logger) : IPhoneNumberService
{
    public bool IsSupported => true;

    public async Task<PhoneDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        if (IsGooglePlayAvailable())
        {
            var hint = await TryPhoneNumberHintAsync(cancellationToken).ConfigureAwait(false);
            if (hint is not null) return hint;
        }
        return await DetectFromSubscriptionsAsync().ConfigureAwait(false);
    }

    private async Task<PhoneDetectionResult?> TryPhoneNumberHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null) return null;
            var client = Identity.GetSignInClient(activity);
            var request = GetPhoneNumberHintIntentRequest.InvokeBuilder().Build();
            var pendingIntent = await client.GetPhoneNumberHintIntent(request).AsAsync<PendingIntent>().ConfigureAwait(false);
            if (pendingIntent is null) return null;

            var result = await ActivityResults.LaunchAsync(
                new ActivityResultContracts.StartIntentSenderForResult(),
                new IntentSenderRequest.Builder(pendingIntent).Build(),
                cancellationToken).ConfigureAwait(false) as ActivityResult;
            if (result is null || result.ResultCode != (int)Result.Ok || result.Data is null)
                return PhoneDetectionResult.Of(PhoneDetectionStatus.Cancelled);

            var number = client.GetPhoneNumberFromIntent(result.Data);
            if (string.IsNullOrWhiteSpace(number)) return null;
            var country = CountryOfDefaultSim();
            return new PhoneDetectionResult(PhoneDetectionStatus.Detected, [new PhoneCandidate(number.Trim(), country, null)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No SIM number known to Google, hint unsupported on this device, etc.: use the fallback.
            logger.LogInformation(ex, "Phone Number Hint unavailable");
            return null;
        }
    }

    private async Task<PhoneDetectionResult> DetectFromSubscriptionsAsync()
    {
        var context = Android.App.Application.Context;
        if (context.GetSystemService(Context.TelephonyService) is not TelephonyManager telephony)
            return PhoneDetectionResult.Of(PhoneDetectionStatus.Unsupported);
        if (telephony.SimState == SimState.Absent)
            return PhoneDetectionResult.Of(PhoneDetectionStatus.NotAvailable);

        var state = await permissions.RequestAsync(AppPermission.PhoneNumber).ConfigureAwait(false);
        if (state != PermissionState.Granted) return PhoneDetectionResult.Of(PhoneDetectionStatus.PermissionDenied);

        var candidates = new List<PhoneCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var subscriptions = context.GetSystemService(Context.TelephonySubscriptionService) as SubscriptionManager;
            foreach (var subId in SubscriptionIds(subscriptions))
            {
                var perSim = telephony.CreateForSubscriptionId(subId);
                if (perSim is null) continue;
                string? number = null;
#pragma warning disable CA1416, CA1422 // Guarded by the API check; Line1Number is the pre-33 way.
                if (OperatingSystem.IsAndroidVersionAtLeast(33) && subscriptions is not null)
                    number = TryGet(() => subscriptions.GetPhoneNumber(subId));
                if (string.IsNullOrWhiteSpace(number)) number = TryGet(() => perSim.Line1Number);
#pragma warning restore CA1416, CA1422
                if (string.IsNullOrWhiteSpace(number)) continue;

                var country = Normalize(TryGet(() => perSim.SimCountryIso));
                var e164 = ToE164(number, country);
                if (!seen.Add(e164)) continue;
                candidates.Add(new PhoneCandidate(e164, country, NullIfEmpty(TryGet(() => perSim.SimOperatorName))));
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Subscription numbers unavailable");
        }

        return candidates.Count > 0
            ? new PhoneDetectionResult(PhoneDetectionStatus.Detected, candidates)
            : PhoneDetectionResult.Of(PhoneDetectionStatus.NotAvailable);
    }

    /// <summary>Active subscription ids; the full list needs READ_PHONE_STATE, so the defaults fill in without it.</summary>
    private static IEnumerable<int> SubscriptionIds(SubscriptionManager? subscriptions)
    {
        var ids = new List<int>();
        if (subscriptions is not null)
        {
            try
            {
                var active = subscriptions.ActiveSubscriptionInfoList;
                if (active is not null)
                    foreach (var info in active)
                        ids.Add(info.SubscriptionId);
            }
            catch (Java.Lang.SecurityException)
            {
            }
        }
        foreach (var id in new[]
                 {
                     SubscriptionManager.DefaultSubscriptionId,
                     SubscriptionManager.DefaultVoiceSubscriptionId,
                     SubscriptionManager.DefaultSmsSubscriptionId,
                     SubscriptionManager.DefaultDataSubscriptionId,
                 })
        {
            if (id >= 0 && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    private static string? CountryOfDefaultSim()
    {
        try
        {
            var telephony = Android.App.Application.Context.GetSystemService(Context.TelephonyService) as TelephonyManager;
            return Normalize(telephony?.SimCountryIso) ?? Normalize(telephony?.NetworkCountryIso);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ToE164(string number, string? country)
    {
        var trimmed = number.Trim();
        if (trimmed.StartsWith('+')) return "+" + new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (country is not null)
        {
            var formatted = PhoneNumberUtils.FormatNumberToE164(trimmed, country);
            if (!string.IsNullOrEmpty(formatted)) return formatted;
        }
        return new string(trimmed.Where(char.IsAsciiDigit).ToArray());
    }

    private static bool IsGooglePlayAvailable()
    {
        try
        {
            return GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(Android.App.Application.Context) == ConnectionResult.Success;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? TryGet(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Normalize(string? iso) =>
        string.IsNullOrWhiteSpace(iso) || iso.Trim().Length != 2 ? null : iso.Trim().ToUpperInvariant();

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
