using System.Net;
using System.Text;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// Every error the API produces is RFC 7807 problem details <c>{status, title, code, errors?}</c> (SPEC §3), whichever
/// layer produced it: model binding, validators, services, authentication, the rate limiter or routing.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ProblemDetailsTests(ApiFactory factory)
{
    private const string ProblemJson = "application/problem+json";

    [Fact]
    public async Task Status_400_from_validators_model_binding_and_services()
    {
        var client = factory.NewClient();

        // FluentValidation (action filter) → errors keyed by camelCase property.
        var validator = await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register,
            new RegisterRequest { Username = SecurityKit.NewUsername("valid"), Password = SecurityKit.Password, DisplayName = new string('x', 65), Device = SecurityKit.Device("x") },
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        validator.HasErrorsFor("displayName").ShouldBeTrue(validator.Root.ToString());
        validator.MediaType.ShouldBe(ProblemJson);

        // Malformed JSON (model binding) on an authenticated endpoint.
        var alice = await factory.RegisterUserAsync("alice");
        using (var malformed = await alice.Client.PostAsync(ApiRoutes.Chats.Groups, new StringContent("{\"title\": ", Encoding.UTF8, "application/json"), SecurityKit.Ct))
        {
            var problem = await malformed.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.Validation);
            problem.Root.TryGetProperty("errors", out _).ShouldBeTrue();
            problem.MediaType.ShouldBe(ProblemJson);
        }

        // A service rule with its own code (reserved username).
        var service = await client.ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register,
            new RegisterRequest { Username = "admin", Password = SecurityKit.Password, DisplayName = "Admin", Device = SecurityKit.Device("x") },
            HttpStatusCode.BadRequest, ErrorCodes.UsernameInvalid);
        service.MediaType.ShouldBe(ProblemJson);

        // Messaging validation with field errors.
        var title = await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.Groups, new CreateGroupRequest { Title = "   " },
            HttpStatusCode.BadRequest, ErrorCodes.Validation);
        title.HasErrorsFor("title").ShouldBeTrue(title.Root.ToString());
    }

    [Fact]
    public async Task Status_401_from_the_service_layer()
    {
        var problem = await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = SecurityKit.NewUsername("ghost"), Password = "not-the-password", Device = SecurityKit.Device("x") },
            HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        problem.MediaType.ShouldBe(ProblemJson);
    }

    /// <summary>401/403/429 written by the authentication handler and the rate limiter use the same shape and media type.</summary>
    [Fact]
    public async Task Status_401_and_429_from_middleware_are_application_problem_json()
    {
        using (var unauthorized = await factory.NewClient().GetAsync(ApiRoutes.Users.Me, SecurityKit.Ct))
            (await unauthorized.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized)).MediaType.ShouldBe(ProblemJson);

        var limited = factory.NewClient();
        for (var i = 0; i < 30; i++)
            (await limited.PostAsync(ApiRoutes.Auth.Refresh, new StringContent("{}", Encoding.UTF8, "application/json"), SecurityKit.Ct)).Dispose();
        using var tooMany = await limited.PostAsync(ApiRoutes.Auth.Refresh, new StringContent("{}", Encoding.UTF8, "application/json"), SecurityKit.Ct);
        (await tooMany.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited)).MediaType.ShouldBe(ProblemJson);
    }

    [Fact]
    public async Task Status_401_from_authentication_middleware_keeps_the_problem_shape()
    {
        using (var anonymous = await factory.NewClient().GetAsync(ApiRoutes.Chats.Root, SecurityKit.Ct))
            await anonymous.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.Unauthorized);

        var alice = await factory.RegisterUserAsync("alice");
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.Logout);
        using var revoked = await alice.Client.GetAsync(ApiRoutes.Chats.Root, SecurityKit.Ct);
        await revoked.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.SessionRevoked);
    }

    [Fact]
    public async Task Status_403_404_409_and_service_429()
    {
        var alice = await factory.RegisterUserAsync("alice");
        var bob = await factory.RegisterUserAsync("bob");

        // 403: bob blocked alice.
        await bob.Client.NoContentAsync(HttpMethod.Put, ApiRoutes.Blocks.ByUserIdPath(alice.Id));
        var forbidden = await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = bob.Id },
            HttpStatusCode.Forbidden, ErrorCodes.UserBlocked);
        forbidden.MediaType.ShouldBe(ProblemJson);

        // 404: unknown chat, unknown media, unknown API route.
        (await alice.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Chats.ByIdPath(Guid.NewGuid()), null, HttpStatusCode.NotFound, ErrorCodes.ChatNotFound))
            .MediaType.ShouldBe(ProblemJson);
        (await alice.Client.ProblemAsync(HttpMethod.Get, ApiRoutes.Media.ByIdPath(Guid.NewGuid()), null, HttpStatusCode.NotFound, ErrorCodes.MediaNotFound))
            .MediaType.ShouldBe(ProblemJson);
        (await alice.Client.ProblemAsync(HttpMethod.Get, "api/nope", null, HttpStatusCode.NotFound, ErrorCodes.NotFound))
            .MediaType.ShouldBe(ProblemJson);

        // 409: username taken (case-insensitively).
        var conflict = await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Register,
            new RegisterRequest { Username = alice.Username.ToUpperInvariant(), Password = SecurityKit.Password, DisplayName = "Copy", Device = SecurityKit.Device("x") },
            HttpStatusCode.Conflict, ErrorCodes.UsernameTaken);
        conflict.MediaType.ShouldBe(ProblemJson);

        // 413: larger than the server accepts.
        var tooLarge = await alice.Client.ProblemAsync(HttpMethod.Post, ApiRoutes.Media.Uploads,
            new CreateUploadRequest { Kind = MediaKind.File, FileName = "huge.bin", ContentType = "application/octet-stream", SizeBytes = long.MaxValue / 2 },
            HttpStatusCode.RequestEntityTooLarge, ErrorCodes.MediaTooLarge);
        tooLarge.MediaType.ShouldBe(ProblemJson);

        // 429 raised by a service (account lockout) rather than the limiter middleware.
        for (var i = 0; i < 10; i++)
        {
            await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
                new LoginRequest { Login = bob.Username, Password = $"wrong-{i}", Device = SecurityKit.Device("x") },
                HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        }
        var locked = await factory.NewClient().ProblemAsync(HttpMethod.Post, ApiRoutes.Auth.Login,
            new LoginRequest { Login = bob.Username, Password = SecurityKit.Password, Device = SecurityKit.Device("x") },
            HttpStatusCode.TooManyRequests, ErrorCodes.RateLimited);
        locked.MediaType.ShouldBe(ProblemJson);
    }
}
