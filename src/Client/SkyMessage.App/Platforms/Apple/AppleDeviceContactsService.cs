using Contacts;
using Foundation;
using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Platform;

namespace SkyMessage.App.Apple;

/// <summary>Phone book via CNContactStore (full or iOS 18 limited access). The OS has no normalized E.164 form.</summary>
public sealed class AppleDeviceContactsService(IPermissionService permissions, ILogger<AppleDeviceContactsService> logger) : IDeviceContactsService
{
    public bool IsSupported => true;

    public async Task<bool> RequestPermissionAsync()
    {
        var state = await permissions.RequestAsync(AppPermission.Contacts).ConfigureAwait(false);
        return state is PermissionState.Granted or PermissionState.Limited;
    }

    public async Task<IReadOnlyList<DeviceContact>> GetContactsAsync(CancellationToken cancellationToken = default)
    {
        var state = await permissions.CheckAsync(AppPermission.Contacts).ConfigureAwait(false);
        if (state is not (PermissionState.Granted or PermissionState.Limited)) return [];
        return await Task.Run(() => Read(cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<DeviceContact> Read(CancellationToken cancellationToken)
    {
        var contacts = new List<DeviceContact>();
        try
        {
            using var store = new CNContactStore();
            var keys = new[]
            {
                CNContactKey.GivenName,
                CNContactKey.FamilyName,
                CNContactKey.MiddleName,
                CNContactKey.Nickname,
                CNContactKey.OrganizationName,
                CNContactKey.PhoneNumbers,
            };
            using var request = new CNContactFetchRequest(keys);
            store.EnumerateContacts(request, out var error, (CNContact contact, ref bool stop) =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    stop = true;
                    return;
                }
                var numbers = contact.PhoneNumbers
                    .Select(n => n.Value?.StringValue?.Trim())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .Distinct()
                    .ToList();
                if (numbers.Count == 0) return;
                var name = string.Join(' ', new[] { contact.GivenName, contact.MiddleName, contact.FamilyName }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
                if (name.Length == 0) name = !string.IsNullOrWhiteSpace(contact.Nickname) ? contact.Nickname : contact.OrganizationName ?? "";
                if (name.Length == 0) name = numbers[0];
                contacts.Add(new DeviceContact(name, numbers));
            });
            if (error is not null) logger.LogWarning("Reading contacts reported {Error}", error.LocalizedDescription);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading the phone book failed");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return contacts.OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
