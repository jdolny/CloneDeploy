using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace CloneDeploy_Server.Backend.Auth;

public static class AuthenticationSettings
{
    // Read once at startup: policy changes require a server restart.
    public static IdentityOptions Read(IConfiguration configuration)
    {
        var password = configuration.GetSection("Authentication:Password");
        var lockout = configuration.GetSection("Authentication:Lockout");
        var options = new IdentityOptions
        {
            Password = new PasswordOptions
            {
                RequiredLength = password.GetValue("RequiredLength", 15),
                RequireDigit = password.GetValue("RequireDigit", false),
                RequireLowercase = password.GetValue("RequireLowercase", false),
                RequireUppercase = password.GetValue("RequireUppercase", false),
                RequireNonAlphanumeric = password.GetValue("RequireNonAlphanumeric", false)
            },
            Lockout = new LockoutOptions
            {
                MaxFailedAccessAttempts = lockout.GetValue("MaxFailedAccessAttempts", 5)
            }
        };
        var minutes = lockout.GetValue("DurationMinutes", 15);
        var errors = new List<string>();
        if (options.Password.RequiredLength < 8)
            errors.Add("Authentication:Password:RequiredLength must be at least 8.");
        if (options.Lockout.MaxFailedAccessAttempts < 1)
            errors.Add("Authentication:Lockout:MaxFailedAccessAttempts must be positive.");
        if (minutes < 1)
            errors.Add("Authentication:Lockout:DurationMinutes must be a positive whole number.");
        if (errors.Count > 0)
            throw new OptionsValidationException("Authentication", typeof(IdentityOptions), errors);
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(minutes);
        return options;
    }
}
