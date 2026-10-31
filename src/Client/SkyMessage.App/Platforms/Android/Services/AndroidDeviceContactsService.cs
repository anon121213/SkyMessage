using Android.Provider;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;
using Phone = Android.Provider.ContactsContract.CommonDataKinds.Phone;

namespace SkyMessage.App.Droid;

/// <summary>Phone book numbers (one query over Phone data rows), with the OS-normalized E.164 form when known.</summary>
public sealed class AndroidDeviceContactsService(IPermissionService permissions, ILogger<AndroidDeviceContactsService> logger) : IDeviceContactsService
{
    public bool IsSupported => true;

    public async Task<bool> RequestPermissionAsync() =>
        await permissions.RequestAsync(AppPermission.Contacts).ConfigureAwait(false) == PermissionState.Granted;

    public async Task<IReadOnlyList<DeviceContact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        if (await permissions.CheckAsync(AppPermission.Contacts).ConfigureAwait(false) != PermissionState.Granted) return [];
        return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<DeviceContact> Read(CancellationToken cancellationToken)
    {
        var byContact = new Dictionary<long, (string Name, List<string> Numbers, List<string> E164)>();
        try
        {
            string[] projection =
            [
                Phone.InterfaceConsts.ContactId,
                Phone.InterfaceConsts.DisplayNamePrimary,
                Phone.Number,
                Phone.NormalizedNumber,
            ];
            using var cursor = Android.App.Application.Context.ContentResolver!.Query(Phone.ContentUri!, projection, null, null, null);
            if (cursor is null) return [];
            var idIndex = cursor.GetColumnIndex(Phone.InterfaceConsts.ContactId);
            var nameIndex = cursor.GetColumnIndex(Phone.InterfaceConsts.DisplayNamePrimary);
            var numberIndex = cursor.GetColumnIndex(Phone.Number);
            var normalizedIndex = cursor.GetColumnIndex(Phone.NormalizedNumber);
            while (cursor.MoveToNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var number = numberIndex >= 0 ? cursor.GetString(numberIndex) : null;
                if (string.IsNullOrWhiteSpace(number)) continue;
                var id = idIndex >= 0 ? cursor.GetLong(idIndex) : byContact.Count;
                var name = nameIndex >= 0 ? cursor.GetString(nameIndex) : null;
                var normalized = normalizedIndex >= 0 ? cursor.GetString(normalizedIndex) : null;

                if (!byContact.TryGetValue(id, out var entry))
                {
                    entry = (string.IsNullOrWhiteSpace(name) ? number.Trim() : name.Trim(), [], []);
                    byContact[id] = entry;
                }
                var trimmed = number.Trim();
                if (!entry.Numbers.Contains(trimmed)) entry.Numbers.Add(trimmed);
                if (!string.IsNullOrWhiteSpace(normalized) && normalized.StartsWith('+') && !entry.E164.Contains(normalized))
                    entry.E164.Add(normalized);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the phone book failed");
        }

        return byContact.Values
            .Select(c => new DeviceContact(c.Name, c.Numbers, c.E164.Count > 0 ? c.E164 : null))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
