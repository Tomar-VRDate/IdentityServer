using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Events;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services;
using Duende.IdentityServer.Validation;
using IdentityModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace IdentityServerHost.Pages.Ciba
{
    [Authorize]
    [SecurityHeadersAttribute]
    public class Consent : PageModel
    {
        private readonly IBackchannelAuthenticationInteractionService _interaction;
        private readonly IEventService _events;
        private readonly ILogger<Index> _logger;

        public Consent(
            IBackchannelAuthenticationInteractionService interaction,
            IEventService events,
            ILogger<Index> logger)
        {
            _interaction = interaction;
            _events = events;
            _logger = logger;
        }

        public ViewModel View { get; set; }
        
        [BindProperty]
        public InputModel Input { get; set; }

        public async Task<IActionResult> OnGet(string id)
        {
            View = await BuildViewModelAsync(id);
            if (View == null)
            {
                return RedirectToPage("/Home/Error/Index");
            }

            Input = new InputModel
            {
                Id = id
            };

            return Page();
        }

        public async Task<IActionResult> OnPost()
        {
            // validate return url is still valid
            var request = await _interaction.GetLoginRequestById(Input.Id);
            if (request == null || request.Subject.GetSubjectId() != User.GetSubjectId())
            {
                _logger.LogError("Invalid id {id}", Input.Id);
                return RedirectToPage("/Home/Error/Index");
            }

            ConsentResponse grantedConsent = null;

            // user clicked 'no' - send back the standard 'access_denied' response
            if (Input?.Button == "no")
            {
                grantedConsent = new ConsentResponse { Error = AuthorizationError.AccessDenied };

                // emit event
                await _events.RaiseAsync(new ConsentDeniedEvent(User.GetSubjectId(), request.Client.ClientId, request.ValidatedResources.RawScopeValues));
            }
            // user clicked 'yes' - validate the data
            else if (Input?.Button == "yes")
            {
                // if the user consented to some scope, build the response model
                if (Input.ScopesConsented != null && Input.ScopesConsented.Any())
                {
                    var scopes = Input.ScopesConsented;
                    if (ConsentOptions.EnableOfflineAccess == false)
                    {
                        scopes = scopes.Where(x => x != Duende.IdentityServer.IdentityServerConstants.StandardScopes.OfflineAccess);
                    }

                    grantedConsent = new ConsentResponse
                    {
                        ScopesValuesConsented = scopes.ToArray(),
                        Description = Input.Description
                    };

                    // emit event
                    await _events.RaiseAsync(new ConsentGrantedEvent(User.GetSubjectId(), request.Client.ClientId, request.ValidatedResources.RawScopeValues, grantedConsent.ScopesValuesConsented, grantedConsent.RememberConsent));
                }
                else
                {
                    ModelState.AddModelError("", ConsentOptions.MustChooseOneErrorMessage);
                }
            }
            else
            {
                ModelState.AddModelError("", ConsentOptions.InvalidSelectionErrorMessage);
            }

            if (grantedConsent != null)
            {
                // communicate outcome of consent back to identityserver
                await _interaction.CompleteRequestByIdAsync(Input.Id, grantedConsent);

                return RedirectToPage("/Ciba/All");
            }

            // we need to redisplay the consent UI
            View = await BuildViewModelAsync(Input.Id, Input);
            return Page();
        }

        private async Task<ViewModel> BuildViewModelAsync(string id, InputModel model = null)
        {
            var request = await _interaction.GetLoginRequestById(id);
            if (request != null && request.Subject.GetSubjectId() == User.GetSubjectId())
            {
                return CreateConsentViewModel(model, id, request);
            }
            else
            {
                _logger.LogError("No backchannel login request matching id: {id}", id);
            }
            return null;
        }

        private ViewModel CreateConsentViewModel(
            InputModel model, string id,
            BackchannelUserLoginRequest request)
        {
            var vm = new ViewModel
            {
                ClientName = request.Client.ClientName ?? request.Client.ClientId,
                ClientUrl = request.Client.ClientUri,
                ClientLogoUrl = request.Client.LogoUri,
                BindingMessage = request.BindingMessage
            };

            vm.IdentityScopes = request.ValidatedResources.Resources.IdentityResources
                .Select(x => CreateScopeViewModel(x, model?.ScopesConsented == null || model.ScopesConsented?.Contains(x.Name) == true))
                .ToArray();

            // TODO: ciba
            var resourceIndicators = /*request.Parameters.GetValues(OidcConstants.AuthorizeRequest.Resource) ??*/ Enumerable.Empty<string>();
            var apiResources = request.ValidatedResources.Resources.ApiResources.Where(x => resourceIndicators.Contains(x.Name));

            var apiScopes = new List<ScopeViewModel>();
            foreach (var parsedScope in request.ValidatedResources.ParsedScopes)
            {
                var apiScope = request.ValidatedResources.Resources.FindApiScope(parsedScope.ParsedName);
                if (apiScope != null)
                {
                    var scopeVm = CreateScopeViewModel(parsedScope, apiScope, model == null || model.ScopesConsented?.Contains(parsedScope.RawValue) == true);
                    scopeVm.Resources = apiResources.Where(x => x.Scopes.Contains(parsedScope.ParsedName))
                        .Select(x => new ResourceViewModel
                        {
                            Name = x.Name,
                            DisplayName = x.DisplayName ?? x.Name,
                        }).ToArray();
                    apiScopes.Add(scopeVm);
                }
            }
            if (ConsentOptions.EnableOfflineAccess && request.ValidatedResources.Resources.OfflineAccess)
            {
                apiScopes.Add(GetOfflineAccessScope(model == null || model.ScopesConsented?.Contains(Duende.IdentityServer.IdentityServerConstants.StandardScopes.OfflineAccess) == true));
            }
            vm.ApiScopes = apiScopes;

            return vm;
        }

        private ScopeViewModel CreateScopeViewModel(IdentityResource identity, bool check)
        {
            return new ScopeViewModel
            {
                Name = identity.Name,
                Value = identity.Name,
                DisplayName = identity.DisplayName ?? identity.Name,
                Description = identity.Description,
                Emphasize = identity.Emphasize,
                Required = identity.Required,
                Checked = check || identity.Required
            };
        }

        public ScopeViewModel CreateScopeViewModel(ParsedScopeValue parsedScopeValue, ApiScope apiScope, bool check)
        {
            var displayName = apiScope.DisplayName ?? apiScope.Name;
            if (!String.IsNullOrWhiteSpace(parsedScopeValue.ParsedParameter))
            {
                displayName += ":" + parsedScopeValue.ParsedParameter;
            }

            return new ScopeViewModel
            {
                Name = parsedScopeValue.ParsedName,
                Value = parsedScopeValue.RawValue,
                DisplayName = displayName,
                Description = apiScope.Description,
                Emphasize = apiScope.Emphasize,
                Required = apiScope.Required,
                Checked = check || apiScope.Required
            };
        }

        private ScopeViewModel GetOfflineAccessScope(bool check)
        {
            return new ScopeViewModel
            {
                Value = Duende.IdentityServer.IdentityServerConstants.StandardScopes.OfflineAccess,
                DisplayName = ConsentOptions.OfflineAccessDisplayName,
                Description = ConsentOptions.OfflineAccessDescription,
                Emphasize = true,
                Checked = check
            };
        }
    }
}
