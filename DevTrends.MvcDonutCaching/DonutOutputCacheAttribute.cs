﻿using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Web;
using System.Web.Configuration;
using System.Web.Mvc;
using System.Web.Routing;
using System.Web.UI;

namespace DevTrends.MvcDonutCaching
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public class DonutOutputCacheAttribute : ActionFilterAttribute, IExceptionFilter
    {
        protected readonly ICacheHeadersHelper CacheHeadersHelper;
        protected readonly ICacheSettingsManager CacheSettingsManager;
        protected readonly IDonutHoleFiller DonutHoleFiller;
        protected readonly IKeyGenerator KeyGenerator;
        protected readonly IReadWriteOutputCacheManager OutputCacheManager;
        protected CacheSettings CacheSettings;

        private bool? _noStore;
        private OutputCacheOptions? _options;

        public DonutOutputCacheAttribute() : this(new KeyBuilder()) { }

        public DonutOutputCacheAttribute(IKeyBuilder keyBuilder) : this(new KeyGenerator(keyBuilder),
                                                                        new OutputCacheManager(OutputCache.Instance, keyBuilder), 
                                                                        GetDonutHoleFiller(),
                                                                        new CacheSettingsManager(),
                                                                        new CacheHeadersHelper())
        { }

        protected DonutOutputCacheAttribute(IKeyGenerator keyGenerator, 
                                            IReadWriteOutputCacheManager outputCacheManager,
                                            IDonutHoleFiller donutHoleFiller, 
                                            ICacheSettingsManager cacheSettingsManager, 
                                            ICacheHeadersHelper cacheHeadersHelper)
        {
            KeyGenerator = keyGenerator;
            OutputCacheManager = outputCacheManager;
            DonutHoleFiller = donutHoleFiller;
            CacheSettingsManager = cacheSettingsManager;
            CacheHeadersHelper = cacheHeadersHelper;

            Duration = -1;
            Location = (OutputCacheLocation)(-1);
            Options = OutputCache.DefaultOptions;
        }

        private static IDonutHoleFiller GetDonutHoleFiller()
        {
            var dataContractSerializer = new DataContractSerializer(typeof(ActionSettings), new[] { typeof(RouteValueDictionary) });
            var dataContractSerializerWrapper = new DataContractSerializerWrapper(dataContractSerializer);
            var actionSettingsSerializer = new ActionSettingsSerialiser(dataContractSerializerWrapper);

            return new DonutHoleFiller(new EncryptingActionSettingsSerialiser(actionSettingsSerializer, new Encryptor()));
        }

        /// <summary>
        /// Gets or sets the cache duration, in seconds.
        /// </summary>
        public int Duration { get; set; }

        /// <summary>
        /// Gets or sets the vary-by-param value.
        /// </summary>
        public string VaryByParam { get; set; }

        /// <summary>
        /// Gets or sets the vary-by-custom value.
        /// </summary>
        public string VaryByCustom { get; set; }

        /// <summary>
        /// Gets or sets the cache profile name.
        /// </summary>
        public string CacheProfile { get; set; }

        /// <summary>
        /// Gets or sets the location.
        /// </summary>
        public OutputCacheLocation Location { get; set; }

        /// <summary>
        /// Gets or sets a value that indicates whether to store the cache.
        /// </summary>
        public bool NoStore
        {
            get { return _noStore ?? false; }
            set { _noStore = value; }
        }

        /// <summary>
        /// Get or sets the <see cref="OutputCacheOptions"/> for this attributes. Specifying a value here will
        /// make the <see cref="OutputCache.DefaultOptions"/> value ignored.
        /// </summary>
        public OutputCacheOptions Options
        {
            get { return _options ?? OutputCacheOptions.None; }
            set { _options = value; }
        }

        public void OnException(ExceptionContext filterContext)
        {
            if (CacheSettings != null)
            {
                ExecuteCallback(filterContext, true);
            }
        }

        /// <summary>
        /// Called before an action method executes.
        /// </summary>
        /// <param name="filterContext">The filter context.</param>
        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            CacheSettings = BuildCacheSettings();

            var cacheKey = KeyGenerator.GenerateKey(filterContext, CacheSettings);

            // If we are unable to generate a cache key it means we can't do anything
            if (string.IsNullOrEmpty(cacheKey))
            {
                return;
            }

            // Are we actually storing data on the server side ?
            if (CacheSettings.IsServerCachingEnabled)
            {
                CacheItem cachedItem = null;

                // If the request is a POST, we lookup for NoCacheLookupForPosts option
                // We are fetching the stored value only if the option has not been set and the request is not a POST
                if (
                    (CacheSettings.Options & OutputCacheOptions.NoCacheLookupForPosts) != OutputCacheOptions.NoCacheLookupForPosts ||
                    filterContext.HttpContext.Request.HttpMethod != "POST"
                )
                {
                    cachedItem = OutputCacheManager.GetItem(cacheKey);
                }

                // We have a cached version on the server side
                if (cachedItem != null)
                {
                    // We inject the previous result into the MVC pipeline
                    // The MVC action won't execute as we injected the previous cached result.
                    filterContext.Result = new ContentResult
                    {
                        Content = DonutHoleFiller.ReplaceDonutHoleContent(cachedItem.Content, filterContext, CacheSettings.Options),
                        ContentType = cachedItem.ContentType
                    };
                }
            }

            // Did we already injected something ?
            if (filterContext.Result != null)
            {
                return; // No need to continue 
            }

            // We are hooking into the pipeline to replace the response Output writer
            // by something we own and later eventually gonna cache
            var cachingWriter = new StringWriter(CultureInfo.InvariantCulture);

            var originalWriter = filterContext.HttpContext.Response.Output;

            filterContext.HttpContext.Response.Output = cachingWriter;

            // Will be called back by OnResultExecuted -> ExecuteCallback
            filterContext.HttpContext.Items[cacheKey] = new Action<bool>(hasErrors =>
            {
                // Removing this executing action from the context
                filterContext.HttpContext.Items.Remove(cacheKey);

                // We restore the original writer for response
                filterContext.HttpContext.Response.Output = originalWriter;

                if (hasErrors)
                {
                    return; // Something went wrong, we are not going to cache something bad
                }

                // Now we use owned caching writer to actually store data
                var cacheItem = new CacheItem
                {
                    Content = cachingWriter.ToString(),
                    ContentType = filterContext.HttpContext.Response.ContentType
                };

                filterContext.HttpContext.Response.Write(
                    DonutHoleFiller.RemoveDonutHoleWrappers(cacheItem.Content, filterContext, CacheSettings.Options)
                );

                if (CacheSettings.IsServerCachingEnabled && filterContext.HttpContext.Response.StatusCode == 200)
                {
                    OutputCacheManager.AddItem(cacheKey, cacheItem, DateTime.UtcNow.AddSeconds(CacheSettings.Duration));
                }
            });
        }

        /// <summary>
        /// Called after an action result executes.
        /// </summary>
        /// <param name="filterContext">The filter context.</param>
        public override void OnResultExecuted(ResultExecutedContext filterContext)
        {
            if (CacheSettings == null) { return; }

            // See OnActionExecuting
            ExecuteCallback(filterContext, filterContext.Exception != null);

            // If we are in the context of a child action, the main action is responsible for setting
            // the right HTTP Cache headers for the final response.
            if (!filterContext.IsChildAction)
            {
                CacheHeadersHelper.SetCacheHeaders(filterContext.HttpContext.Response, CacheSettings);
            }
        }

        /// <summary>
        /// Builds the cache settings.
        /// </summary>
        /// <returns></returns>
        /// <exception cref="System.Web.HttpException">
        /// The 'duration' attribute must have a value that is greater than or equal to zero.
        /// </exception>
        protected CacheSettings BuildCacheSettings()
        {
            CacheSettings cacheSettings;

            if (string.IsNullOrEmpty(CacheProfile))
            {
                cacheSettings = GetDefaultCacheSettings();
            }
            else
            {
                var outputCacheProfile = CacheSettingsManager.RetrieveOutputCacheProfile(CacheProfile);

                cacheSettings = GetCacheSettingsFromCacheProfile(outputCacheProfile);
            }

            if (cacheSettings.Duration == -1)
            {
                throw new HttpException("The directive or the configuration settings profile must specify the 'duration' attribute.");
            }

            if (cacheSettings.Duration < 0)
            {
                throw new HttpException("The 'duration' attribute must have a value that is greater than or equal to zero.");
            }

            return cacheSettings;
        }

        private CacheSettings GetDefaultCacheSettings()
        {
            return new CacheSettings
            {
                IsCachingEnabled = CacheSettingsManager.IsCachingEnabledGlobally,
                Duration = Duration,
                VaryByCustom = VaryByCustom,
                VaryByParam = VaryByParam,
                Location = (int)Location == -1 ? OutputCacheLocation.Server : Location,
                NoStore = NoStore,
                Options = Options,
            };
        }

        private CacheSettings GetCacheSettingsFromCacheProfile(OutputCacheProfile outputCacheProfile)
        {
            return new CacheSettings
            {
                IsCachingEnabled = CacheSettingsManager.IsCachingEnabledGlobally && outputCacheProfile.Enabled,
                Duration = Duration == -1 ? outputCacheProfile.Duration : Duration,
                VaryByCustom = VaryByCustom ?? outputCacheProfile.VaryByCustom,
                VaryByParam = VaryByParam ?? outputCacheProfile.VaryByParam,
                Location = (int)Location == -1 ? ((int)outputCacheProfile.Location == -1 ? OutputCacheLocation.Server : outputCacheProfile.Location) : Location,
                NoStore = _noStore ?? outputCacheProfile.NoStore,
                Options = Options,
            };
        }

        /// <summary>
        /// Executes the callback.
        /// </summary>
        /// <param name="context">The context.</param>
        /// <param name="hasErrors">if set to <c>true</c> [has errors].</param>
        private void ExecuteCallback(ControllerContext context, bool hasErrors)
        {
            var cacheKey = KeyGenerator.GenerateKey(context, CacheSettings);

            if (string.IsNullOrEmpty(cacheKey)) { return; }

            var callback = context.HttpContext.Items[cacheKey] as Action<bool>;

            if (callback != null)
            {
                callback.Invoke(hasErrors);
            }
        }
    }
}