﻿using Microsoft.Rtc.Internal.RestAPI.Common.MediaTypeFormatters;
using Microsoft.Rtc.Internal.RestAPI.ResourceModel;
using Microsoft.SfB.PlatformService.SDK.Common;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Formatting;
using System.Threading.Tasks;

namespace Microsoft.SfB.PlatformService.SDK.ClientModel
{
    public abstract class EventableEntity
    {
        /// <summary>
        /// HandleResourceEvent for current resource
        /// </summary>
        /// <param name="eventcontext"></param>
        internal abstract void HandleResourceEvent(EventContext eventcontext);

        /// <summary>
        /// ProcessAndDispatchEventsToChild
        /// </summary>
        /// <param name="eventContext"></param>
        internal virtual bool ProcessAndDispatchEventsToChild(EventContext eventContext)
        {
            return false;
        }
    }

    public abstract class BasePlatformResource<TPlatformResource, TCapabilities> : EventableEntity, IPlatformResource<TCapabilities>
        where TPlatformResource : Resource
    {
        #region Constructor

        /// <summary>
        /// Initializes a new instance of the BaseInstance class.
        /// </summary>
        /// <param name="restfulClient">The restful client.</param>
        /// <param name="resource">The platform resource.</param>
        /// <param name="baseUri">The base uri.</param>
        internal BasePlatformResource(IRestfulClient restfulClient, TPlatformResource resource, Uri baseUri, Uri resourceUri, object parent)
        {
            if (restfulClient == null)
            {
                throw new ArgumentNullException(nameof(restfulClient));
            }

            if (baseUri == null)
            {
                throw new ArgumentNullException(nameof(baseUri));
            }

            this.RestfulClient = restfulClient;
            this.PlatformResource = resource;
            this.BaseUri = baseUri;
            this.Parent = parent;

            if (resourceUri!=null)
            {
                this.ResourceUri = UriHelper.CreateAbsoluteUri(this.BaseUri, resourceUri.ToString());
            }

            string typeName = this.GetType().ToString();
            Logger.Instance.Information(string.Format("{0} resource created, resource Uri {1}", typeName, this.ResourceUri));
        }

        #endregion

        #region Protected properties

        /// <summary>
        /// Gets the PlatformResource.
        /// </summary>
        protected TPlatformResource PlatformResource { get; private set; }

        //Unit test can leverage this to avoid waiting too long
        internal TimeSpan WaitForEvents { get; set; } = TimeSpan.FromSeconds(30);

        #endregion

        #region Public properties

        /// <summary>
        /// Gets the BaseUri.
        /// </summary>
        public Uri BaseUri { get; private set; }

        /// <summary>
        /// Gets the local resource uri.
        /// </summary>
        public Uri ResourceUri { get; private set; }

        /// <summary>
        /// The parent resource
        /// </summary>
        public object Parent { get; private set; }

        /// <summary>
        /// Updated Event Handlers
        /// </summary>
        public EventHandler<PlatformResourceEventArgs> HandleResourceUpdated { get; set; }

        /// <summary>
        /// Completed Event Handlers
        /// </summary>
        public EventHandler<PlatformResourceEventArgs> HandleResourceCompleted { get; set; }

        /// <summary>
        /// Resource Removed Event Handlers
        /// </summary>
        public EventHandler<PlatformResourceEventArgs> HandleResourceRemoved { get; set; }

        #endregion

        #region Internal properties

        /// <summary>
        /// Gets the Restful Client.
        /// </summary>
        internal IRestfulClient RestfulClient { get; private set; }

        #endregion

        #region Public methods

        /// <summary>
        /// Get Platform Resource from PlatformService directly.
        /// </summary>
        /// <typeparam name="TResource">The type of related platform resource.</typeparam>
        /// <param name="requestUri">The request uri for the related platform resource.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The related platform resource.</returns>
        public async Task RefreshAsync(LoggingContext loggingContext)
        {
            string typeName = this.GetType().Name;
            Logger.Instance.Information("Calling " + typeName + " RefreshAsync" + (loggingContext == null ? string.Empty : loggingContext.ToString()));

            TPlatformResource result = await this.GetLatestPlatformServiceResourceAsync<TPlatformResource>(this.ResourceUri, loggingContext).ConfigureAwait(false);
            this.PlatformResource = result;
            this.HandleResourceUpdated?.Invoke(this, new PlatformResourceEventArgs { Operation = EventOperation.Updated, PlatformResource = result });
        }

        /// <summary>
        /// Get Platform Resource from PlatformService directly.
        /// </summary>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The related platform resource.</returns>
        public async Task DeleteAsync(LoggingContext loggingContext)
        {
            string typeName = this.GetType().Name;
            Logger.Instance.Information("Calling " + typeName + " DeleteAsync");
            await this.DeleteRelatedPlatformResourceAsync(this.ResourceUri, loggingContext).ConfigureAwait(false);
            this.HandleResourceRemoved?.Invoke(this, new PlatformResourceEventArgs { Operation = EventOperation.Deleted });
        }

        /// <summary>
        /// Gets whether a particular capability is available or not.
        /// </summary>
        /// <param name="capability">Capability that needs to be checked.</param>
        /// <returns><code>true</code> iff the capability is available as of now.</returns>
        /// <remarks>
        /// Capabilities can change when a resource is updated. So, this method returning <code>true</code> doesn't guarantee that
        /// the capability will be available when it is actually used. Make sure to catch <see cref="CapabilityNotAvailableException"/>
        /// </remarks>
        public abstract bool Supports(TCapabilities capability);

        #endregion

        #region Protected methods

        /// <summary>
        /// Get Platform Resource from PlatformService directly.
        /// </summary>
        /// <typeparam name="TResource">The type of related platform resource.</typeparam>
        /// <param name="requestUri">The request uri for the related platform resource.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The related platform resource.</returns>
        protected async Task<TResource> GetLatestPlatformServiceResourceAsync<TResource>(
            Uri requestUri,
            LoggingContext loggingContext)
            where TResource : Resource
        {
            LoggingContext localLoggingContext = loggingContext == null ? new LoggingContext() : loggingContext.Clone() as LoggingContext;

            var customerHeaders = new Dictionary<string, string>();

            Logger.Instance.Information("calling  GetLatestPlatformServiceResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext?.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId);
            }

            DateTime startTime = DateTime.UtcNow;

            var result = default(TResource);
            try
            {
                var httpResponse = await RestfulClient.GetAsync(requestUri, customerHeaders).ConfigureAwait(false);

                localLoggingContext.FillTracingInfoFromHeaders(httpResponse.Headers, false);
                if (httpResponse.IsSuccessStatusCode ||
                (httpResponse.StatusCode == HttpStatusCode.NotFound &&
                    !string.IsNullOrWhiteSpace(loggingContext.PlatformResponseCorrelationId) &&
                    !string.IsNullOrWhiteSpace(loggingContext.PlatformResponseServerFqdn)))
                {
                    if (httpResponse.StatusCode != HttpStatusCode.NoContent && httpResponse.StatusCode != HttpStatusCode.NotFound && httpResponse.Content != null)
                    {
                        var platformResourceStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        result = MediaTypeFormattersHelper.ReadContentWithType(typeof(TResource), httpResponse.Content.Headers.ContentType, platformResourceStream) as TResource;
                    }
                }
                else
                {
                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponse, loggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex,
                    "calling  GetLatestPlatformServiceResourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
            return result;
        }

        /// <summary>
        /// Create related Platform Resource.
        /// </summary>
        /// <param name="requestUri">The request uri.</param>
        /// <param name="content">The content which you posted.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The HttpResponse Message.</returns>
        protected async Task<HttpResponseMessage> PostRelatedPlatformResourceAsync(Uri requestUri, HttpContent content, LoggingContext loggingContext)
        {
            LoggingContext localLoggingContext = loggingContext == null ? null : loggingContext.Clone() as LoggingContext;

            var customerHeaders = new Dictionary<string, string>();

            if (localLoggingContext == null)
            {
                localLoggingContext = new LoggingContext();
            }

            Logger.Instance.Information("calling" + this.GetType().Name + "  PostRelatedPlatformResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext?.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId);
            }

            var startTime = DateTime.UtcNow;
            var serverSideTrackingGuid = string.Empty;
            try
            {
                var httpResponseMesssage = await this.RestfulClient.PostAsync(requestUri, content, customerHeaders).ConfigureAwait(false);

                localLoggingContext.FillTracingInfoFromHeaders(httpResponseMesssage.Headers, false);
                if (!httpResponseMesssage.IsSuccessStatusCode)
                {
                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponseMesssage, localLoggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }

                return httpResponseMesssage;
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex,
                      "calling  PostRelatedPlatformResourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
        }

        /// <summary>
        /// Create the related Platform Resource.
        /// </summary>
        /// <typeparam name="TInput">The type of platform input resource.</typeparam>
        /// <param name="requestUri">The request uri.</param>
        /// <param name="input">The platform input resource.</param>
        /// <param name="mediaTypeFormatter">The media type formatter to serialize.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The task.</returns>
        protected async Task<HttpResponseMessage> PostRelatedPlatformResourceAsync<TInput>(Uri requestUri, TInput input, MediaTypeFormatter mediaTypeFormatter, LoggingContext loggingContext)
            where TInput : class
        {
            LoggingContext localLoggingContext = loggingContext == null ? null : loggingContext.Clone() as LoggingContext;

            var customerHeaders = new Dictionary<string, string>();

            if (localLoggingContext == null)
            {
                localLoggingContext = new LoggingContext();
            }

            Logger.Instance.Information("calling" + this.GetType().Name + "  PostRelatedPlatformResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext?.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId);
            }

            try
            {
                var httpResponse = await this.RestfulClient.PostAsync(requestUri, input, mediaTypeFormatter, customerHeaders).ConfigureAwait(false);

                localLoggingContext.FillTracingInfoFromHeaders(httpResponse.Headers, false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponse, localLoggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }
                return httpResponse;
            }
            catch (Exception ex)
            {
                //handle and rethrow exception
                Logger.Instance.Error(ex,
                       "calling  PostRelatedPlatformResourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
        }

        /// <summary>
        /// Update the related Platform Resource.
        /// </summary>
        /// <param name="requestUri">The request uri.</param>
        /// <param name="content">The content which you put.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The task.</returns>
        protected async Task PutRelatedPlatformResourceAsync(Uri requestUri, HttpContent content, LoggingContext loggingContext)
        {
            var customerHeaders = new Dictionary<string, string>();
            LoggingContext localLoggingContext = loggingContext == null ? null : loggingContext.Clone() as LoggingContext;

            if (localLoggingContext == null)
            {
                localLoggingContext = new LoggingContext();
            }

            Logger.Instance.Information("calling" + this.GetType().Name + "  PutRelatedPlatformResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId);
            }

            var startTime = DateTime.UtcNow;

            try
            {
                var httpResponse = await this.RestfulClient.PutAsync(requestUri, content, customerHeaders).ConfigureAwait(false);
                localLoggingContext.FillTracingInfoFromHeaders(httpResponse.Headers, false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponse, localLoggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex,
                      "calling  PutRelatedPlatformesourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
        }

        /// <summary>
        /// Update the related Platform Resource.
        /// </summary>
        /// <typeparam name="TInput">The type of platform input resource.</typeparam>
        /// <param name="requestUri">The request uri.</param>
        /// <param name="input">The platform input resource.</param>
        /// <param name="mediaTypeFormatter">The media type formatter to serialize.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The task.</returns>
        protected async Task<HttpResponseMessage> PutRelatedPlatformResourceAsync<TInput>(Uri requestUri, TInput input, MediaTypeFormatter mediaTypeFormatter, LoggingContext loggingContext)
            where TInput : class
        {
            LoggingContext localLoggingContext = loggingContext == null ? null : loggingContext.Clone() as LoggingContext;

            var customerHeaders = new Dictionary<string, string>();

            if (localLoggingContext == null)
            {
                localLoggingContext = new LoggingContext();
            }

            Logger.Instance.Information("calling" + this.GetType().Name + "  PutRelatedPlatformResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext?.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId.ToString());
            }

            try
            {
                var httpResponse = await this.RestfulClient.PutAsync(requestUri, input, mediaTypeFormatter, customerHeaders).ConfigureAwait(false);

                localLoggingContext.FillTracingInfoFromHeaders(httpResponse.Headers, false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponse, localLoggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }
                return httpResponse;
            }
            catch (Exception ex)
            {
                //handle and rethrow exception
                Logger.Instance.Error(ex,
                       "calling  PostRelatedPlatformResourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
        }

        /// <summary>
        /// Delete the related Platform Resource.
        /// </summary>
        /// <param name="requestUri">The request uri.</param>
        /// <param name="loggingContext">The logging context.</param>
        /// <returns>The task.</returns>
        protected async Task DeleteRelatedPlatformResourceAsync(Uri requestUri, LoggingContext loggingContext)
        {
            LoggingContext localLoggingContext = loggingContext == null ? null : loggingContext.Clone() as LoggingContext;
            var customerHeaders = new Dictionary<string, string>();

            if (localLoggingContext == null)
            {
                localLoggingContext = new LoggingContext();
            }

            Logger.Instance.Information("calling" + this.GetType().Name + "  DeleteRelatedPlatformResourceAsync " + requestUri.ToString() + "\r\n" + localLoggingContext.ToString());

            localLoggingContext.PropertyBag[Constants.RemotePlatformServiceUri] = requestUri;
            if (!string.IsNullOrEmpty(loggingContext?.JobId))
            {
                customerHeaders.Add(Constants.UcapClientRequestId, loggingContext.JobId);
            }

            try
            {
                var httpResponse = await this.RestfulClient.DeleteAsync(requestUri, customerHeaders).ConfigureAwait(false);
                localLoggingContext.FillTracingInfoFromHeaders(httpResponse.Headers, false);
                if (!httpResponse.IsSuccessStatusCode)
                {
                    if (httpResponse.StatusCode == HttpStatusCode.NotFound &&
                    !string.IsNullOrWhiteSpace(loggingContext.PlatformResponseCorrelationId) &&
                    !string.IsNullOrWhiteSpace(loggingContext.PlatformResponseServerFqdn))
                    {
                        return;
                    }

                    var serverSideEx = await RemotePlatformServiceException.ConvertToRemotePlatformServiceExceptionAsync(httpResponse, loggingContext).ConfigureAwait(false);
                    if (serverSideEx != null)
                    {
                        throw serverSideEx;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Error(ex,
                       "calling  DeleteRelatedPlatformResourceAsync " + requestUri.ToString() + " ERROR\r\n" + localLoggingContext.ToString());
                throw;
            }
        }

        /// <summary>
        /// Convert to PlatformService resource
        /// </summary>
        /// <param name="eventContext"></param>
        /// <returns></returns>
        protected virtual T ConvertToPlatformServiceResource<T>(EventContext eventContext) where T : Resource
        {
            T result = null;
            if (eventContext.EventEntity.EmbeddedResource != null)
            {
                result = eventContext.EventEntity.EmbeddedResource.ConvertToType<T>();
            }

            return result;
        }

        #endregion

        #region Internal methods

        internal override void HandleResourceEvent(EventContext eventcontext)
        {
            Logger.Instance.Information(string.Format("[{0}]: HandleResourceEvent: sender: {1}, senderHref: {2}, EventResourceName: {3} EventFullHref: {4}, EventType: {5} ,LoggingContext: {6}",
            this.GetType().Name,eventcontext.SenderResourceName, eventcontext.SenderHref, eventcontext.EventResourceName, eventcontext.EventFullHref, eventcontext.EventEntity.Relationship.ToString(), eventcontext.LoggingContext == null ? string.Empty : eventcontext.LoggingContext.ToString()));

            TPlatformResource resource = this.ConvertToPlatformServiceResource<TPlatformResource>(eventcontext);//Will get null for deleted event

            if (resource != null)
            {
                this.PlatformResource = resource;
            }

            var eventArgs = new PlatformResourceEventArgs
            {
                Operation = eventcontext.EventEntity.Relationship,
                PlatformResource = resource
            };

            EventHandler<PlatformResourceEventArgs> handler = null;

            switch (eventcontext.EventEntity.Relationship)
            {
                case EventOperation.Updated   : handler = HandleResourceUpdated  ; break;
                case EventOperation.Completed : handler = HandleResourceCompleted; break;
                case EventOperation.Deleted   : handler = HandleResourceRemoved  ; break;
            }

            handler?.Invoke(this, eventArgs);
        }

        #endregion
    }

    /// <summary>
    /// PlatformResourceEventArgs
    /// </summary>
    public class PlatformResourceEventArgs : EventArgs
    {
        /// <summary>
        /// Operation
        /// </summary>
        public  EventOperation Operation { get; internal set; }

        /// <summary>
        /// The resource snapshot
        /// </summary>
        public Resource PlatformResource { get; internal set; }
    }
}

