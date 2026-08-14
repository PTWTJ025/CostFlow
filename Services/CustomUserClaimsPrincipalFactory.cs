using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using CostFlow.Models;

namespace CostFlow.Services;

public class CustomUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>
{
    public CustomUserClaimsPrincipalFactory(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> optionsAccessor)
        : base(userManager, roleManager, optionsAccessor)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (!string.IsNullOrEmpty(user.FullName))
        {
            identity.AddClaim(new Claim("FullName", user.FullName));
        }

        if (!string.IsNullOrEmpty(user.ProfilePictureUrl))
        {
            identity.AddClaim(new Claim("ProfilePictureUrl", user.ProfilePictureUrl));
        }

        // เพิ่ม Role Claim ให้บัญชีหลักอัตโนมัติ เพื่อความเสถียร 100%
        if (string.Equals(user.UserName, "ADMIN01", StringComparison.OrdinalIgnoreCase))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
            identity.AddClaim(new Claim("role", "Admin"));
        }
        else if (string.Equals(user.UserName, "DEV01", StringComparison.OrdinalIgnoreCase))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Dev"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
            identity.AddClaim(new Claim("role", "Dev"));
            identity.AddClaim(new Claim("role", "Admin"));
        }
        else if (string.Equals(user.UserName, "STAFF01", StringComparison.OrdinalIgnoreCase))
        {
            identity.AddClaim(new Claim(ClaimTypes.Role, "Staff"));
            identity.AddClaim(new Claim("role", "Staff"));
        }

        return identity;
    }
}
