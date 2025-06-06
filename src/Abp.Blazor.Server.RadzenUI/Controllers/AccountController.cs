﻿using System.Net.Mail;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp;
using Volo.Abp.Account;
using Volo.Abp.Auditing;
using Volo.Abp.Identity;
using Volo.Abp.Identity.AspNetCore;
using Volo.Abp.Security.Claims;
using static Volo.Abp.Identity.Settings.IdentitySettingNames;

namespace Abp.RadzenUI.Controllers;

public class AccountController(
    SignInManager<Volo.Abp.Identity.IdentityUser> signInManager,
    IdentitySecurityLogManager identitySecurityLogManager,
    IAccountAppService accountAppService,
    IdentityUserManager userManager,
    IdentityDynamicClaimsPrincipalContributorCache identityDynamicClaimsPrincipalContributorCache,
    IOptions<IdentityOptions> identityOptions
) : AbpRadzenControllerBase
{
    [HttpPost("/account/login")]
    [DisableAuditing]
    public async Task<IActionResult> LoginAsync(string username, string password, bool rememberMe)
    {
        try
        {
            var result = await signInManager.PasswordSignInAsync(
                username,
                password,
                rememberMe,
                true
            );

            await identitySecurityLogManager.SaveAsync(
                new IdentitySecurityLogContext()
                {
                    Identity = IdentitySecurityLogIdentityConsts.Identity,
                    Action = result.ToIdentitySecurityLogAction(),
                    UserName = username
                }
            );

            if (result.Succeeded)
            {
                return Redirect("~/");
            }

            return RedirectWithError("~/login", result.GetResultAsString());
        }
        catch (Exception ex)
        {
            return RedirectWithError("~/login", ex);
        }
    }

    [HttpPost("/account/externallogin")]
    public async Task<IActionResult> ExternalLoginAsync(string provider, string returnUrl)
    {
        try
        {
            var redirectUrl = Url.Action("ExternalLoginCallback", "Account", new { returnUrl });
            var properties = signInManager.ConfigureExternalAuthenticationProperties(
                provider,
                redirectUrl
            );
            properties.Items["scheme"] = provider;

            return await Task.FromResult(Challenge(properties, provider));
        }
        catch (Exception ex)
        {
            return RedirectWithError("~/login", ex);
        }
    }

    [HttpGet("/account/externallogin/callback")]
    public async Task<IActionResult> ExternalLoginCallbackAsync(
        string returnUrl = "",
        string returnUrlHash = "",
        string remoteError = ""
    )
    {
        try
        {
            if (!remoteError.IsNullOrEmpty())
            {
                Logger.LogWarning("External login callback error: {remoteError}", remoteError);
                return RedirectWithError("~/login", remoteError);
            }

            await identityOptions.SetAsync();

            var loginInfo = await signInManager.GetExternalLoginInfoAsync();
            if (loginInfo == null)
            {
                Logger.LogWarning("External login info is not available");
                return RedirectWithError("~/login", "External login info is not available");
            }

            var result = await signInManager.ExternalLoginSignInAsync(
                loginInfo.LoginProvider,
                loginInfo.ProviderKey,
                isPersistent: false,
                bypassTwoFactor: true
            );

            if (!result.Succeeded)
            {
                await identitySecurityLogManager.SaveAsync(
                    new IdentitySecurityLogContext()
                    {
                        Identity = IdentitySecurityLogIdentityConsts.IdentityExternal,
                        Action = "Login" + result
                    }
                );
            }

            if (result.IsLockedOut)
            {
                Logger.LogWarning("External login callback error: user is locked out!");
                return RedirectWithError(
                    "~/login",
                    "External login callback error: user is locked out!"
                );
            }

            if (result.IsNotAllowed)
            {
                Logger.LogWarning("External login callback error: user is not allowed!");
                return RedirectWithError(
                    "~/login",
                    "External login callback error: user is not allowed!"
                );
            }

            Volo.Abp.Identity.IdentityUser? user;
            if (result.Succeeded)
            {
                user = await userManager.FindByLoginAsync(
                    loginInfo.LoginProvider,
                    loginInfo.ProviderKey
                );
                if (user != null)
                {
                    await identityDynamicClaimsPrincipalContributorCache.ClearAsync(
                        user.Id,
                        user.TenantId
                    );
                }

                return await RedirectSafelyAsync(returnUrl, returnUrlHash);
            }

            var email =
                loginInfo.Principal.FindFirstValue(AbpClaimTypes.Email)
                ?? loginInfo.Principal.FindFirstValue(ClaimTypes.Email);
            if (email.IsNullOrWhiteSpace())
            {
                return Redirect(
                    $"~/register?IsExternalLogin=true&ExternalLoginAuthSchema={loginInfo.LoginProvider}&ReturnUrl={returnUrl}"
                );
            }

            user = await userManager.FindByEmailAsync(email);
            if (user == null)
            {
                var (userName, emailAddress) = await TryGetUserInfoAsync();
                return Redirect(
                    $"~/register?IsExternalLogin=true&ExternalLoginAuthSchema={loginInfo.LoginProvider}&ReturnUrl={returnUrl}&UserName={userName}&EmailAddress={emailAddress}"
                );
            }

            if (
                await userManager.FindByLoginAsync(loginInfo.LoginProvider, loginInfo.ProviderKey)
                == null
            )
            {
                var identityResult = await userManager.AddLoginAsync(user, loginInfo);
                if (!identityResult.Succeeded)
                {
                    Logger.LogWarning("Add Login Error:{@Errors}", identityResult.Errors);
                    return RedirectWithError(
                        "~/login",
                        identityResult
                            .Errors.Select(e => $"[{e.Code}] {e.Description}")
                            .JoinAsString(", ")
                    );
                }
            }

            await signInManager.SignInAsync(user, false);

            await identitySecurityLogManager.SaveAsync(
                new IdentitySecurityLogContext()
                {
                    Identity = IdentitySecurityLogIdentityConsts.IdentityExternal,
                    Action = result.ToIdentitySecurityLogAction(),
                    UserName = user.Name
                }
            );

            await identityDynamicClaimsPrincipalContributorCache.ClearAsync(user.Id, user.TenantId);

            return await RedirectSafelyAsync(returnUrl, returnUrlHash);
        }
        catch (Exception ex)
        {
            return RedirectWithError("~/login", ex);
        }
    }

    [HttpGet("/account/logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        await signInManager.SignOutAsync();
        await HttpContext.SignOutAsync();
        return Redirect("~/Login");
    }

    [HttpPost("/account/register")]
    public async Task<IActionResult> RegisterAsync(
        string userName,
        string emailAddress,
        string password,
        bool isExternalLogin,
        string externalLoginAuthSchema
    )
    {
        try
        {
            if (isExternalLogin)
            {
                var externalLoginInfo = await signInManager.GetExternalLoginInfoAsync();
                if (externalLoginInfo == null)
                {
                    Logger.LogWarning("External login info is not available");
                    return RedirectWithError("~/login", "External login info is not available");
                }
                if (userName.IsNullOrWhiteSpace())
                {
                    userName = await userManager.GetUserNameFromEmailAsync(emailAddress);
                }
                await RegisterExternalUserAsync(
                    externalLoginInfo,
                    userName,
                    emailAddress,
                    externalLoginAuthSchema
                );
            }
            else
            {
                await RegisterLocalUserAsync(userName, emailAddress, password);
            }

            return Redirect("~/");
        }
        catch (Exception ex)
        {
            return RedirectWithError(
                $"~/register?IsExternalLogin={isExternalLogin}&ExternalLoginAuthSchema={externalLoginAuthSchema}&ReturnUrl=/&UserName={userName}&EmailAddress={emailAddress}",
                ex
            );
        }
    }

    private async Task RegisterLocalUserAsync(string userName, string emailAddress, string password)
    {
        var userDto = await accountAppService.RegisterAsync(
            new RegisterDto
            {
                AppName = "BlazorServer WebApp",
                EmailAddress = emailAddress,
                UserName = userName,
                Password = password
            }
        );

        var user = await userManager.GetByIdAsync(userDto.Id);
        await signInManager.SignInAsync(user, isPersistent: true);

        // Clear the dynamic claims cache.
        await identityDynamicClaimsPrincipalContributorCache.ClearAsync(user.Id, user.TenantId);
    }

    protected async Task RegisterExternalUserAsync(
        ExternalLoginInfo externalLoginInfo,
        string userName,
        string emailAddress,
        string externalLoginAuthSchema
    )
    {
        await identityOptions.SetAsync();

        var user = new Volo.Abp.Identity.IdentityUser(
            GuidGenerator.Create(),
            userName,
            emailAddress,
            CurrentTenant.Id
        );

        (await userManager.CreateAsync(user)).CheckErrors();
        (await userManager.AddDefaultRolesAsync(user)).CheckErrors();

        var userLoginAlreadyExists = user.Logins.Any(x =>
            x.TenantId == user.TenantId
            && x.LoginProvider == externalLoginInfo.LoginProvider
            && x.ProviderKey == externalLoginInfo.ProviderKey
        );

        if (!userLoginAlreadyExists)
        {
            (
                await userManager.AddLoginAsync(
                    user,
                    new UserLoginInfo(
                        externalLoginInfo.LoginProvider,
                        externalLoginInfo.ProviderKey,
                        externalLoginInfo.ProviderDisplayName
                    )
                )
            ).CheckErrors();
        }

        await signInManager.SignInAsync(user, isPersistent: true, externalLoginAuthSchema);

        // Clear the dynamic claims cache.
        await identityDynamicClaimsPrincipalContributorCache.ClearAsync(user.Id, user.TenantId);
    }

    private async Task<(string userName, string emailAddress)> TryGetUserInfoAsync()
    {
        var externalLoginInfo = await signInManager.GetExternalLoginInfoAsync();
        if (externalLoginInfo == null)
        {
            return (string.Empty, string.Empty);
        }

        if (!externalLoginInfo.Principal.Identities.Any())
        {
            return (string.Empty, string.Empty);
        }

        var identity = externalLoginInfo.Principal.Identities.First();
        var emailClaim =
            identity.FindFirst(AbpClaimTypes.Email) ?? identity.FindFirst(ClaimTypes.Email);

        if (emailClaim == null)
        {
            return (string.Empty, string.Empty);
        }

        var userName = await userManager.GetUserNameFromEmailAsync(emailClaim.Value);
        return (userName, emailClaim.Value);
    }
}
