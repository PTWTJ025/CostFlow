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

        return identity;
    }
}
