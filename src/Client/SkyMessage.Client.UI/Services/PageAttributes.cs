namespace SkyMessage.Client.UI.Services;

/// <summary>
/// Marks a routable page as reachable while signed out (welcome, login, register). Every other page requires a
/// session: Routes.razor redirects signed-out users to /welcome (remembering profile/invite links), and sends
/// signed-in users away from anonymous pages. Usage: <c>@attribute [AnonymousPage]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class AnonymousPageAttribute : Attribute;
