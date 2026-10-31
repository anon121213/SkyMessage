namespace SkyMessage.Contracts;

/// <summary>Validation limits shared by the server and all clients.</summary>
public static class Limits
{
    public const int UsernameMinLength = 5;
    public const int UsernameMaxLength = 32;
    /// <summary>Latin letter first, then letters, digits or underscore.</summary>
    public const string UsernamePattern = "^[a-zA-Z][a-zA-Z0-9_]{4,31}$";

    public const int PasswordMinLength = 8;
    public const int PasswordMaxLength = 128;

    public const int DisplayNameMaxLength = 64;
    public const int BioMaxLength = 200;
    public const int ContactNameMaxLength = 64;

    /// <summary>Chat ids in one <see cref="ReorderPinnedChatsRequest"/>.</summary>
    public const int MaxPinnedChatsReorder = 1000;

    public const int GroupTitleMaxLength = 128;
    public const int GroupDescriptionMaxLength = 500;
    public const int MaxGroupMembers = 1000;

    public const int MessageTextMaxLength = 4096;
    public const int MaxAttachmentsPerMessage = 10;
    public const int MaxEntitiesPerMessage = 100;
    public const int MaxMessagesPerBatch = 100;
    public const int MessagesPageDefault = 50;
    public const int MessagesPageMax = 100;
    public const int MessageChangesMax = 500;
    public const int EditWindowHours = 48;
    public const int ReactionMaxLength = 16;
    public const int MaxReactionUserIds = 100;
    public const int ReplyPreviewTextLength = 120;
    public const int LinkUrlMaxLength = 2048;

    public const int DefaultChunkSize = 1024 * 1024;
    public const long DefaultMaxUploadBytes = 2L * 1024 * 1024 * 1024;
    public const int FileNameMaxLength = 255;
    public const int VideoNoteMaxDurationSeconds = 60;
    public const int VoiceMaxDurationSeconds = 3600;
    public const int WaveformSamples = 100;

    public const int TypingTimeoutSeconds = 6;
    public const int CallRingTimeoutSeconds = 45;

    public const int FindByPhonesMax = 1000;
    public const int MaxBatchUsers = 200;
    public const int SearchQueryMinLength = 2;
    /// <summary>Global (non-contact) user search only matches username prefixes at least this long.</summary>
    public const int GlobalUsernameSearchMinLength = 3;
    public const int SearchResultsMax = 50;
    public const int DeviceNameMaxLength = 128;
}
