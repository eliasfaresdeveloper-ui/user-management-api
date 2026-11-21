using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// 1. Standardized error responses (ProblemDetails)
builder.Services.AddProblemDetails();

// 2. Token-based (JWT Bearer) authentication & authorization
var signingKey = builder.Configuration["Jwt:Key"] ?? "dev-only-secret-key-change-in-production-32chars!";
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

// Middleware pipeline (order matters for both correctness and performance)
app.UseExceptionHandler();

app.Use(async (context, next) =>
{
    var startedAt = DateTime.UtcNow;
    var method = context.Request.Method;
    var path = context.Request.Path;

    await next();

    var durationMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
    var statusCode = context.Response.StatusCode;
    var user = context.User.Identity?.Name ?? "anonymous";

    app.Logger.LogInformation(
        "AUDIT | User: {User} | {Method} {Path} -> {StatusCode} in {DurationMs:0.0}ms",
        user,
        method,
        path,
        statusCode,
        durationMs);
});

app.UseAuthentication();
app.UseAuthorization();

var users = new List<User>
{
    new(1, "John Doe", "john@example.com"),
    new(2, "Jane Smith", "jane@example.com")
};

// Group endpoints and require a valid token for all /users routes
var usersGroup = app.MapGroup("/users").RequireAuthorization();

// GET all users
usersGroup.MapGet("/", Results<Ok<List<User>>, ProblemHttpResult> () =>
{
    try
    {
        return TypedResults.Ok(users);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error getting users");
        return TypedResults.Problem("Something went wrong while loading users.");
    }
});

// GET user by id
usersGroup.MapGet("/{id:int}", Results<Ok<User>, NotFound<string>, ProblemHttpResult> (int id) =>
{
    try
    {
        var user = users.FirstOrDefault(x => x.Id == id);
        return user is null
            ? TypedResults.NotFound($"User with ID {id} not found.")
            : TypedResults.Ok(user);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Error finding user {Id}", id);
        return TypedResults.Problem("Something went wrong while looking up the user.");
    }
});

// POST user
usersGroup.MapPost("/", Results<Created<User>, BadRequest<string>> (User user) =>
{
    var validationError = ValidateUser(user);
    if (validationError is not null)
    {
        return TypedResults.BadRequest(validationError);
    }

    var nextId = users.Count > 0 ? users.Max(x => x.Id) + 1 : 1;
    var newUser = user with { Id = nextId, Name = user.Name.Trim(), Email = user.Email.Trim() };
    users.Add(newUser);

    return TypedResults.Created($"/users/{newUser.Id}", newUser);
});

// PUT user
usersGroup.MapPut("/{id:int}", Results<Ok<User>, NotFound<string>, BadRequest<string>> (int id, User updatedUser) =>
{
    var validationError = ValidateUser(updatedUser);
    if (validationError is not null)
    {
        return TypedResults.BadRequest(validationError);
    }

    var existingUser = users.FirstOrDefault(x => x.Id == id);
    if (existingUser is null)
    {
        return TypedResults.NotFound($"User with ID {id} not found.");
    }

    var index = users.IndexOf(existingUser);
    users[index] = updatedUser with { Id = id, Name = updatedUser.Name.Trim(), Email = updatedUser.Email.Trim() };

    return TypedResults.Ok(users[index]);
});

// DELETE user
usersGroup.MapDelete("/{id:int}", Results<NoContent, NotFound<string>> (int id) =>
{
    var user = users.FirstOrDefault(x => x.Id == id);
    if (user is null)
    {
        return TypedResults.NotFound($"User with ID {id} not found.");
    }

    users.Remove(user);
    return TypedResults.NoContent();
});

app.Run();

static string? ValidateUser(User user)
{
    if (string.IsNullOrWhiteSpace(user.Name))
    {
        return "Name is required.";
    }

    if (string.IsNullOrWhiteSpace(user.Email) || !user.Email.Contains('@'))
    {
        return "A valid email is required.";
    }

    return null;
}

public record User(
    int Id,
    string Name,
    string Email);