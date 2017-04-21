using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using Sitecore;
using Sitecore.Analytics;
using Sitecore.Data;
using Sitecore.Diagnostics;
using Sitecore.Modules.EmailCampaign;
using Sitecore.Modules.EmailCampaign.Attributes;
using Sitecore.Modules.EmailCampaign.Core;
using Sitecore.Modules.EmailCampaign.Diagnostics;
using Sitecore.Modules.EmailCampaign.Exceptions;
using Sitecore.Modules.EmailCampaign.Factories;
using Sitecore.Modules.EmailCampaign.Models.SubscriptionForm;
using Sitecore.Modules.EmailCampaign.Recipients;
using Sitecore.Modules.EmailCampaign.Xdb;
using Sitecore.Mvc.Controllers;
using Sitecore.Mvc.Presentation;
using Sitecore.Resources;
using System;
using System.Collections.Generic;
using System.Configuration.Provider;
using System.Linq;
using System.Web.Mvc;

namespace Sitecore.Support.Modules.EmailCampaign.Controllers.SubscriptionForm
{
  public class SubscriptionFormController: SitecoreController
  {
    private readonly bool confirmSubscription;
    private readonly EcmFactory factory;
    private readonly bool isPageInNormalMode;
    private readonly ILogger logger;
    private readonly RecipientRepository recipientRepository;
    private readonly SubscriptionFormRenderingParamaneters renderingParameters;

    public SubscriptionFormController() : this(Logger.Instance, EcmFactory.GetDefaultFactory())
    {
    }

    internal SubscriptionFormController(ILogger logger, EcmFactory factory)
    {
      this.confirmSubscription = true;
      Assert.ArgumentNotNull(logger, "logger");
      Assert.ArgumentNotNull(factory, "factory");
      this.logger = logger;
      this.factory = factory;
      this.recipientRepository = RecipientRepository.GetDefaultInstance();
      this.renderingParameters = (RenderingContext.CurrentOrNull != null) ? SubscriptionFormRenderingParamaneters.Parse(RenderingContext.CurrentOrNull.Rendering.Parameters) : null;
      this.isPageInNormalMode = Context.PageMode.IsNormal;
    }

    protected virtual ID GetContactId()
    {
      //if()
      return new ID(Tracker.Current.Contact.ContactId);
    }

    protected virtual ID GetContactId(string email)
    {
      Guid? anonymousIdFromEmail = ClientApi.GetAnonymousIdFromEmail(email);
      return (anonymousIdFromEmail.HasValue ? new ID(anonymousIdFromEmail.Value) : this.GetContactId());
    }

    private SubscriptionFormViewModel GetDefaultSubscriptionFormViewModel(string email)
    {
      SubscriptionFormViewModel model = new SubscriptionFormViewModel
      {
        RenderingParameters = this.renderingParameters
      };
      model.EmailLabelText = model.RenderingParameters.ShowList ? Util.GetFrontEndText("email address") : Util.GetFrontEndText("simple title");
      model.SubscribeButtonText = Util.GetFrontEndText("subscribe");
      model.ListHeaderText = Util.GetFrontEndText("newsletters");
      SubscriptionInfo[] subscriptionInfos = this.GetSubscriptionInfos(email, model.RenderingParameters.IncludeRecipientLists);
      if ((subscriptionInfos != null) && Enumerable.Any<SubscriptionInfo>(subscriptionInfos))
      {
        model.SubscriptionInfos = subscriptionInfos;
        model.EmailAddress = this.GetRecipientEmail() ?? string.Empty;
        model.EmailTextBoxHelptext = Util.GetFrontEndText("enter email");
      }
      else
      {
        model.WarningText = Util.GetFrontEndText("no newsletters");
      }
      model.SubscribeImage = Images.GetThemedImageSource("Applications/24x24/bullet_triangle_green.png");
      return model;
    }

    protected virtual string GetRecipientEmail(string email = null)
    {
      if (!this.isPageInNormalMode)
      {
        return string.Empty;
      }
      Recipient recipientSpecific = this.recipientRepository.GetRecipientSpecific(string.IsNullOrEmpty(email) ? this.GetRecipientId() : GetRecipientId(email), new Type[] { typeof(Email) });
      if (recipientSpecific == null)
      {
        return null;
      }
      Email defaultProperty = recipientSpecific.GetProperties<Email>().DefaultProperty;
      return defaultProperty?.EmailAddress;
    }

    protected virtual RecipientId GetRecipientId()
    {
      RecipientId id = null;
      ID contactId = this.GetContactId();
      if (contactId != (ID)null)
      {
        id = new XdbContactId(contactId);
      }
      return id;
    }

    protected virtual RecipientId GetRecipientId(string email)
    {
      RecipientId id = null;
      ID contactId = this.GetContactId(email);
      if (contactId != (ID)null)
      {
        id = new XdbContactId(contactId);
      }
      return id;
    }

    protected virtual SubscriptionInfo[] GetSubscriptionInfos(string emailAddress, string includeRecipientLists)
    {
      if (string.IsNullOrEmpty(includeRecipientLists))
      {
        return new SubscriptionInfo[0];
      }
      string[] contactListIds = (from listId in ID.ParseArray(includeRecipientLists) select listId.ToString()).ToArray<string>();
      return ClientApi.GetSubscriptionInfo(this.isPageInNormalMode ? this.GetRecipientId(emailAddress) : null, contactListIds);
    }

    [HttpGet]
    public ActionResult Index()
    {
      SubscriptionFormViewModel defaultSubscriptionFormViewModel = this.GetDefaultSubscriptionFormViewModel(string.Empty);
      return base.View(defaultSubscriptionFormViewModel);
    }

    [ValidateFormHandler, HttpPost]
    public ActionResult Index(SubcriptionFormSubmission submissionValuesModel)
    {
      SubscriptionFormViewModel defaultSubscriptionFormViewModel = this.GetDefaultSubscriptionFormViewModel(submissionValuesModel.EmailAddress ?? string.Empty);
      if (!string.IsNullOrEmpty(submissionValuesModel.EmailAddress))
      {
        defaultSubscriptionFormViewModel.EmailAddress = submissionValuesModel.EmailAddress;
        //GetContactId(submissionValuesModel.EmailAddress);
      }
      try
      {
        if (submissionValuesModel.ConfirmEmailChange)
        {
          string emailAddress = submissionValuesModel.EmailAddress;
          if (!(string.IsNullOrEmpty(this.GetRecipientEmail()) && !emailAddress.Equals(this.GetRecipientEmail(), StringComparison.OrdinalIgnoreCase)))
          {
            this.UpdateEmailInXdb(submissionValuesModel.EmailAddress);
          }
        }
        string recipientEmail = this.GetRecipientEmail(submissionValuesModel.EmailAddress);
        if (!string.IsNullOrEmpty(recipientEmail) && !submissionValuesModel.EmailAddress.Equals(recipientEmail, StringComparison.OrdinalIgnoreCase))
        {
          if (!Util.IsValidEmail(submissionValuesModel.EmailAddress))
          {
            throw new EmailCampaignException(Util.GetFrontEndText("email not valid"));
          }
          string frontEndText = Util.GetFrontEndText("address changed confirmation");
          defaultSubscriptionFormViewModel.ConfirmationMessage = string.Format(frontEndText, Util.GetFrontEndText("an email"));
          return base.View(defaultSubscriptionFormViewModel);
        }
        if (string.IsNullOrEmpty(recipientEmail))
        {
          this.UpdateEmailInXdb(submissionValuesModel.EmailAddress);
        }
        List<string> list = new List<string>();
        List<string> list2 = new List<string>();
        if (defaultSubscriptionFormViewModel.RenderingParameters.ShowList)
        {
          foreach (Guid guid in from subscriptionInfo in defaultSubscriptionFormViewModel.SubscriptionInfos select Guid.Parse(subscriptionInfo.ContactListId))
          {
            if (Enumerable.Contains<Guid>(submissionValuesModel.SubscriptionIds, guid))
            {
              list.Add(guid.ToString());
            }
            else
            {
              list2.Add(guid.ToString());
            }
          }
        }
        else
        {
          SubscriptionInfo[] subscriptionInfos = this.GetSubscriptionInfos(submissionValuesModel.EmailAddress, this.renderingParameters.IncludeRecipientLists);
          for (int i = 0; i < subscriptionInfos.Length; i = (int)(i + 1))
          {
            list.Add(subscriptionInfos[i].ContactListId);
          }
        }
        string itemId = ClientApi.UpdateSubscriptions(this.GetRecipientId(submissionValuesModel.EmailAddress), list.ToArray(), list2.ToArray(), defaultSubscriptionFormViewModel.RenderingParameters.ManagerRootId, this.confirmSubscription);
        if (!string.IsNullOrEmpty(itemId))
        {
          string contentItemPageUrl = ItemUtilExt.GetContentItemPageUrl(itemId);
          if (!string.IsNullOrEmpty(contentItemPageUrl))
          {
            return this.Redirect(contentItemPageUrl);
          }
        }
      }
      catch (EmailCampaignException exception)
      {
        defaultSubscriptionFormViewModel.WarningText = exception.LocalizedMessage;
      }
      catch (ProviderException)
      {
        defaultSubscriptionFormViewModel.WarningText = Util.GetFrontEndText("email in use");
      }
      catch (Exception exception2)
      {
        defaultSubscriptionFormViewModel.WarningText = exception2.Message;
        this.logger.LogError(exception2);
      }
      return base.View(defaultSubscriptionFormViewModel);
    }

    protected virtual void UpdateEmailInXdb(string emailAddress)
    {
      this.recipientRepository.UpdateRecipientEmail(this.GetRecipientId(), emailAddress);
    }
  }
}