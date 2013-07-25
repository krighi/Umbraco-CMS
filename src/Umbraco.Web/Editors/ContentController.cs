﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Web.Http.ModelBinding;
using AutoMapper;
using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Publishing;
using Umbraco.Core.Services;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Models.Mapping;
using Umbraco.Web.Mvc;
using Umbraco.Web.Security;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Binders;
using Umbraco.Web.WebApi.Filters;
using umbraco;

namespace Umbraco.Web.Editors
{
    /// <summary>
    /// The API controller used for editing content
    /// </summary>
    [PluginController("UmbracoApi")]
    public class ContentController : ContentControllerBase
    {
        private readonly ContentModelMapper _contentModelMapper;

        /// <summary>
        /// Constructor
        /// </summary>
        public ContentController()
            : this(UmbracoContext.Current, new ContentModelMapper(UmbracoContext.Current.Application, new UserModelMapper()))
        {            
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="umbracoContext"></param>
        /// <param name="contentModelMapper"></param>
        internal ContentController(UmbracoContext umbracoContext, ContentModelMapper contentModelMapper)
            : base(umbracoContext)
        {
            _contentModelMapper = contentModelMapper;
        }

        public IEnumerable<ContentItemDisplay> GetByIds([FromUri]int[] ids)
        {
            var foundContent = ((ContentService) Services.ContentService).GetByIds(ids);

            return foundContent.Select(Mapper.Map<IContent, ContentItemDisplay>);
        }

        /// <summary>
        /// Gets the content json for the content id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public ContentItemDisplay GetById(int id)
        {
            var foundContent = Services.ContentService.GetById(id);
            if (foundContent == null)
            {
                HandleContentNotFound(id);
            }
            return Mapper.Map<IContent, ContentItemDisplay>(foundContent);
        }

        /// <summary>
        /// Gets an empty content item for the 
        /// </summary>
        /// <param name="contentTypeAlias"></param>
        /// <param name="parentId"></param>
        /// <returns></returns>
        public ContentItemDisplay GetEmpty(string contentTypeAlias, int parentId)
        {
            var contentType = Services.ContentTypeService.GetContentType(contentTypeAlias);
            if (contentType == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var emptyContent = new Content("", parentId, contentType);
            return Mapper.Map<IContent, ContentItemDisplay>(emptyContent);
        }

        /// <summary>
        /// Saves content
        /// </summary>
        /// <returns></returns>
        [FileUploadCleanupFilter]
        public ContentItemDisplay PostSave(
            [ModelBinder(typeof(ContentItemBinder))]
                ContentItemSave<IContent> contentItem)
        {
            //If we've reached here it means:
            // * Our model has been bound
            // * and validated
            // * any file attachments have been saved to their temporary location for us to use
            // * we have a reference to the DTO object and the persisted object

            UpdateName(contentItem);
            
            //TODO: We need to support 'send to publish'

            //TODO: We'll need to save the new template, publishat, etc... values here

            MapPropertyValues(contentItem);

            //We need to manually check the validation results here because:
            // * We still need to save the entity even if there are validation value errors
            // * Depending on if the entity is new, and if there are non property validation errors (i.e. the name is null)
            //      then we cannot continue saving, we can only display errors
            // * If there are validation errors and they were attempting to publish, we can only save, NOT publish and display 
            //      a message indicating this
            if (!ModelState.IsValid)
            {
                if (ValidationHelper.ModelHasRequiredForPersistenceErrors(contentItem)
                    && (contentItem.Action == ContentSaveAction.SaveNew || contentItem.Action == ContentSaveAction.PublishNew))
                {
                    //ok, so the absolute mandatory data is invalid and it's new, we cannot actually continue!
                    // add the modelstate to the outgoing object and throw a 403
                    var forDisplay = Mapper.Map<IContent, ContentItemDisplay>(contentItem.PersistedContent);
                    forDisplay.Errors = ModelState.ToErrorDictionary();
                    throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.Forbidden, forDisplay));
                    
                }

                //if the model state is not valid we cannot publish so change it to save
                switch (contentItem.Action)
                {
                    case ContentSaveAction.Publish:
                        contentItem.Action = ContentSaveAction.Save;
                        break;
                    case ContentSaveAction.PublishNew:
                        contentItem.Action = ContentSaveAction.SaveNew;
                        break;
                }
            }

            //initialize this to successful
            var publishStatus = new Attempt<PublishStatus>(true, null);

            if (contentItem.Action == ContentSaveAction.Save || contentItem.Action == ContentSaveAction.SaveNew)
            {
                //save the item
                Services.ContentService.Save(contentItem.PersistedContent);
            }
            else
            {
                //publish the item and check if it worked, if not we will show a diff msg below
                publishStatus = ((ContentService)Services.ContentService).SaveAndPublishInternal(contentItem.PersistedContent);
            }
            

            //return the updated model
            var display = Mapper.Map<IContent, ContentItemDisplay>(contentItem.PersistedContent);

            //lasty, if it is not valid, add the modelstate to the outgoing object and throw a 403
            HandleInvalidModelState(display);

            //put the correct msgs in 
            switch (contentItem.Action)
            {
                case ContentSaveAction.Save:
                case ContentSaveAction.SaveNew:
                    display.AddSuccessNotification(ui.Text("speechBubbles", "editContentSavedHeader"), ui.Text("speechBubbles", "editContentSavedText"));
                    break;
                case ContentSaveAction.Publish:
                case ContentSaveAction.PublishNew:
                    ShowMessageForStatus(publishStatus.Result, display);
                    break;
            }

            return display;
        }

        private void ShowMessageForStatus(PublishStatus status, ContentItemDisplay display)
        {
            switch (status.StatusType)
            {
                case PublishStatusType.Success:
                case PublishStatusType.SuccessAlreadyPublished:
                    display.AddSuccessNotification(
                        ui.Text("speechBubbles", "editContentPublishedHeader", UmbracoUser),
                        ui.Text("speechBubbles", "editContentPublishedText", UmbracoUser));
                    break;
                case PublishStatusType.FailedPathNotPublished:
                    display.AddWarningNotification(
                        ui.Text("publish"),
                        ui.Text("publish", "contentPublishedFailedByParent",
                                string.Format("{0} ({1})", status.ContentItem.Name, status.ContentItem.Id),
                                UmbracoUser).Trim());
                    break;
                case PublishStatusType.FailedCancelledByEvent:
                    display.AddWarningNotification(
                        ui.Text("publish"),
                        ui.Text("speechBubbles", "contentPublishedFailedByEvent"));
                    break;
                case PublishStatusType.FailedHasExpired:
                case PublishStatusType.FailedAwaitingRelease:
                case PublishStatusType.FailedIsTrashed:
                case PublishStatusType.FailedContentInvalid:
                    display.AddWarningNotification(
                        ui.Text("publish"),
                        ui.Text("publish", "contentPublishedFailedInvalid",
                                new[]
                                    {
                                        string.Format("{0} ({1})", status.ContentItem.Name, status.ContentItem.Id),
                                        string.Join(",", status.InvalidProperties.Select(x => x.Alias))
                                    },
                                UmbracoUser).Trim());
                    break;
                default:
                    throw new IndexOutOfRangeException();
            }
        }

    }
}