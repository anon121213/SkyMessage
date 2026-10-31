namespace SkyMessage.Domain.Entities;

/// <summary>Key/value facts about this deployment, e.g. <see cref="InstanceIdKey"/>.</summary>
public sealed class ServerProperty
{
    public const string InstanceIdKey = "instance_id";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
