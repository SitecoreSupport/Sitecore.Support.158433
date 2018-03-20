namespace Sitecore.Support.Modules.EmailCampaign.Controllers.SubscriptionForm
{
    using System;
    using System.Collections.Generic;
    using System.Configuration.Provider;
    using System.Linq;
    using System.Web.Mvc;
    using Sitecore.Analytics;
    using Sitecore.Data;
    using Sitecore.Diagnostics;
    using Sitecore.EmailCampaign.Model.Exceptions;
    using Sitecore.ExM.Framework.Diagnostics;
    using Sitecore.Modules.EmailCampaign;
    using Sitecore.Modules.EmailCampaign.Attributes;
    using Sitecore.Modules.EmailCampaign.Core;
    using Sitecore.Modules.EmailCampaign.Factories;
    using Sitecore.Modules.EmailCampaign.Models.SubscriptionForm;
    using Sitecore.Modules.EmailCampaign.Recipients;
    using Sitecore.Modules.EmailCampaign.Validators;
    using Sitecore.Modules.EmailCampaign.Xdb;
    using Sitecore.Mvc.Controllers;
    using Sitecore.Mvc.Presentation;
    using Sitecore.Resources;

    public class SubscriptionFormController : SitecoreController
    {

        private readonly bool _confirmSubscription = true;

        private readonly RecipientRepository _recipientRepository;

        private readonly SubscriptionFormRenderingParameters _renderingParameters;

        private readonly bool _isPageInNormalMode;

        private readonly EcmFactory _factory;

        [NotNull]
        private readonly ILogger _logger;

        [NotNull]
        private readonly RegexValidator _emailRegexValidator;

        public SubscriptionFormController()
            : this(Logger.Instance, EcmFactory.GetDefaultFactory(),
                (RegexValidator)Configuration.Factory.CreateObject("emailRegexValidator", true))
        {
        }

        internal SubscriptionFormController([NotNull] ILogger logger, [NotNull] EcmFactory factory, [NotNull] RegexValidator emailRegexValidator)
        {
            Assert.ArgumentNotNull(logger, "logger");
            Assert.ArgumentNotNull(factory, "factory");
            Assert.ArgumentNotNull(emailRegexValidator, "emailRegexValidator");

            _logger = logger;
            _factory = factory;
            _emailRegexValidator = emailRegexValidator;
            _recipientRepository = RecipientRepository.GetDefaultInstance();
            _renderingParameters = RenderingContext.CurrentOrNull != null
                ? SubscriptionFormRenderingParameters.Parse(RenderingContext.CurrentOrNull.Rendering.Parameters)
                : null;
            _isPageInNormalMode = Context.PageMode.IsNormal;
        }

        [HttpGet]
        public new ActionResult Index()
        {
            var model = GetDefaultSubscriptionFormViewModel();

            return View("~/layouts/EmailCampaign/MVC/SubscriptionForm/index.cshtml", model);
        }

        [HttpPost]
        [ValidateFormHandler]
        public ActionResult Index(SubcriptionFormSubmission submissionValuesModel)
        {
            var model = GetDefaultSubscriptionFormViewModel();
            if (!string.IsNullOrEmpty(submissionValuesModel.EmailAddress))
            {
                model.EmailAddress = submissionValuesModel.EmailAddress;
            }

            try
            {
                if (submissionValuesModel.ConfirmEmailChange)
                {
                    var email = submissionValuesModel.EmailAddress;

                    if (!string.IsNullOrEmpty(GetRecipientEmail())
                        && !email.Equals(GetRecipientEmail(), StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateEmailInXdb(submissionValuesModel.EmailAddress);
                    }
                }

                var currentContactsEmailAddress = GetRecipientEmail();

                if (!string.IsNullOrEmpty(currentContactsEmailAddress)
                    && !submissionValuesModel.EmailAddress.Equals(
                        currentContactsEmailAddress,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!_emailRegexValidator.IsValid(submissionValuesModel.EmailAddress))
                    {
                        throw new EmailCampaignException(Util.GetFrontEndText("email not valid"));
                    }

                    var text = Util.GetFrontEndText("address changed confirmation");
                    model.ConfirmationMessage = string.Format(text, Util.GetFrontEndText("an email"));

                    return View("~/layouts/EmailCampaign/MVC/SubscriptionForm/index.cshtml", model);
                }

                if (string.IsNullOrEmpty(currentContactsEmailAddress))
                {
                    Sitecore.Analytics.Tracker.Current.Session.Identify(submissionValuesModel.EmailAddress);
                    UpdateEmailInXdb(submissionValuesModel.EmailAddress);
                }

                var listsToSubscribe = new List<string>();
                var listsToUnsubscribe = new List<string>();

                if (model.RenderingParameters.ShowList)
                {
                    foreach (var info in model.SubscriptionInfos)
                    {
                        var recipientListId = Guid.Parse(info.ContactListId);
                        var isTicked = submissionValuesModel.SubscriptionIds.Contains(recipientListId);

                        if (isTicked && !info.UserSubscribed)
                        {
                            listsToSubscribe.Add(recipientListId.ToString());
                        }
                        else if (!isTicked && info.UserSubscribed)
                        {
                            listsToUnsubscribe.Add(recipientListId.ToString());
                        }
                    }
                }
                else
                {
                    var subscriptionInfos = GetSubscriptionInfos(submissionValuesModel.EmailAddress, _renderingParameters.IncludeRecipientLists);

                    for (var i = 0; i < subscriptionInfos.Length; i++)
                    {
                        listsToSubscribe.Add(subscriptionInfos[i].ContactListId);
                    }
                }

                var itemIdToRedirect = ClientApi.UpdateSubscriptions(GetRecipientId(submissionValuesModel.EmailAddress),
                    listsToSubscribe.ToArray(),
                    listsToUnsubscribe.ToArray(),
                    model.RenderingParameters.ManagerRootId,
                    _confirmSubscription);

                if (!string.IsNullOrEmpty(itemIdToRedirect))
                {
                    var url = ItemUtilExt.GetContentItemPageUrl(itemIdToRedirect);
                    if (!string.IsNullOrEmpty(url))
                    {
                        return Redirect(url);
                    }
                }
            }
            catch (EmailCampaignException e)
            {
                model.WarningText = e.LocalizedMessage;
            }
            catch (ProviderException)
            {
                model.WarningText = Util.GetFrontEndText("email in use");
            }
            catch (Exception e)
            {
                model.WarningText = e.Message;
                _logger.LogError(e);
            }

            return View("~/layouts/EmailCampaign/MVC/SubscriptionForm/index.cshtml", model);
        }

        protected virtual ID GetContactId(string email)
        {
            var anonymousId = ClientApi.GetAnonymousIdFromEmail(email);

            return anonymousId.HasValue ? new ID(anonymousId.Value) : GetContactId();
        }

        protected virtual ID GetContactId()
        {
            return new ID(Tracker.Current.Contact.ContactId);
        }

        protected virtual string GetRecipientEmail()
        {
            if (!_isPageInNormalMode)
            {
                return string.Empty;
            }

            var recipient = _recipientRepository.GetRecipientSpecific(GetRecipientId(), typeof(Email));

            if (recipient == null)
            {
                return null;
            }

            var email = recipient.GetProperties<Email>().DefaultProperty;

            if (email == null)
            {
                return null;
            }

            return email.EmailAddress;
        }

        protected virtual RecipientId GetRecipientId(string email)
        {
            RecipientId recipientId = null;

            var contactId = GetContactId(email);

            if (contactId != (ID)null)
            {
                recipientId = new XdbContactId(contactId);
            }

            return recipientId;
        }

        protected virtual RecipientId GetRecipientId()
        {
            RecipientId recipientId = null;

            var contactId = GetContactId();

            if (contactId != (ID)null)
            {
                recipientId = new XdbContactId(contactId);
            }

            return recipientId;
        }

        protected virtual SubscriptionInfo[] GetSubscriptionInfos(string emailAddress, string includeRecipientLists)
        {
            if (string.IsNullOrEmpty(includeRecipientLists))
            {
                return new SubscriptionInfo[0];
            }

            var listsIds = ID.ParseArray(includeRecipientLists);

            var contactListIds = listsIds.Select(listId => listId.ToString()).ToArray();

            return ClientApi.GetSubscriptionInfo(_isPageInNormalMode ? GetRecipientId(emailAddress) : null, contactListIds);
        }

        protected virtual void UpdateEmailInXdb(string emailAddress)
        {
            _recipientRepository.UpdateRecipientEmail(GetRecipientId(), emailAddress);
        }

        private SubscriptionFormViewModel GetDefaultSubscriptionFormViewModel()
        {
            var model = new SubscriptionFormViewModel
            {
                RenderingParameters = _renderingParameters
            };

            model.EmailLabelText = model.RenderingParameters.ShowList
                ? Util.GetFrontEndText("email address")
                : Util.GetFrontEndText("simple title");
            model.SubscribeButtonText = Util.GetFrontEndText("subscribe");
            model.ListHeaderText = Util.GetFrontEndText("newsletters");

            var subscriptionInfos = GetSubscriptionInfos(string.Empty, model.RenderingParameters.IncludeRecipientLists);
            if (subscriptionInfos != null && subscriptionInfos.Any())
            {
                model.SubscriptionInfos = subscriptionInfos;
                model.EmailAddress = GetRecipientEmail() ?? string.Empty;
                model.EmailTextBoxHelptext = Util.GetFrontEndText("enter email");
            }
            else
            {
                model.WarningText = Util.GetFrontEndText("no newsletters");
            }

            model.SubscribeImage = Images.GetThemedImageSource("Applications/24x24/bullet_triangle_green.png");
            return model;
        }
    }
}
