#region Copyright (c) 2010 Atif Aziz. All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//    http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
#endregion

#region Assembly Information

using System.Reflection;
using System.Runtime.InteropServices;
using System.Web;
using Elmah.Bootstrapper;

[assembly: AssemblyTitle("Elmah.Bootstrapper")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ELMAH")]
[assembly: AssemblyCopyright("Copyright \u00a9 2010 Atif Aziz. All rights reserved.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion("1.0.18606.0")]
[assembly: AssemblyFileVersion("1.0.18606.622")]

#if DEBUG
[assembly: AssemblyConfiguration("DEBUG")]
#else
[assembly: System.Reflection.AssemblyConfiguration("RELEASE")]
#endif

#endregion

[assembly: PreApplicationStartMethod(typeof(Ignition), "Start")]

namespace Elmah.Bootstrapper
{
    #region Imports

    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.ComponentModel.Design;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Web;
    using System.Web.Hosting;
    using Microsoft.Web.Infrastructure.DynamicModuleHelper;

    #endregion

    sealed class ErrorLogHandlerMappingModule : HttpModuleBase
    {
        ErrorLogPageFactory _errorLogPageFactory;

        ErrorLogPageFactory HandlerFactory
        {
            get { return _errorLogPageFactory ?? (_errorLogPageFactory = new ErrorLogPageFactory()); }
        }

        protected override void OnInit(HttpApplication application)
        {
            application.Subscribe(h => application.PostMapRequestHandler += h, OnPostMapRequestHandler);
            application.Subscribe(h => application.EndRequest += h, OnEndRequest);
        }

        void OnPostMapRequestHandler(HttpContextBase context)
        {
            var request = context.Request;

            var url = request.FilePath;
            var match = Regex.Match(url, @"(/(?:.*\b)?(?:elmah|errors|errorlog))(/.+)?$",
                                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                             .BindNum((fst, snd) => new { fst.Success, Url = fst.Value, PathInfo = snd.Value });

            if (!match.Success)
                return;

            url = match.Url;
            // ReSharper disable once PossibleNullReferenceException
            var queryString = request.Url.Query;

            context.RewritePath(url, match.PathInfo,
                                queryString.Length > 0 && queryString[0] == '?'
                                ? queryString.Substring(1)
                                : queryString);

            var pathTranslated = request.PhysicalApplicationPath;
            var factory = HandlerFactory;
            var handler = factory.GetHandler(context, request.HttpMethod, url, pathTranslated);
            if (handler == null)
                return;

            context.Items[this] = new ContextState
            {
                Handler = handler,
                HandlerFactory = factory,
            };

            context.Handler = handler;
        }

        void OnEndRequest(HttpContextBase context)
        {
            var state = context.Items[this] as ContextState;
            if (state == null)
                return;
            state.HandlerFactory.ReleaseHandler(state.Handler);
        }

        sealed class ContextState
        {
            public IHttpHandler Handler;
            public IHttpHandlerFactory HandlerFactory;
        }
    }

    public static class Ignition
    {
        static readonly object Lock = new object();

        static bool _registered;

        public static void Start()
        {
            lock (Lock)
            {
                if (_registered)
                    return;
                StartImpl();
                _registered = true;
            }
        }

        static void StartImpl()
        {
            // TODO Consider what happens if registration fails halfway

            ServiceCenter.Current = GetServiceProvider;

            foreach (var type in DefaultModuleTypeSet)
                RegisterModule(type);
        }

        static void RegisterModule(Type moduleType)
        {
#if NET40
            DynamicModuleUtility.RegisterModule(moduleType);
#else
            HttpApplication.RegisterModule(moduleType);
#endif
        }

        static IEnumerable<Type> DefaultModuleTypeSet
        {
            get
            {
                yield return typeof(ErrorLogModule);
                yield return typeof(ErrorMailModule);
                yield return typeof(ErrorFilterModule);
                yield return typeof(ErrorTweetModule);
                yield return typeof(ErrorLogHandlerMappingModule);
            }
        }

        public static IServiceProvider GetServiceProvider(object context)
        {
            return GetServiceProvider(AsHttpContextBase(context));
        }

        static HttpContextBase AsHttpContextBase(object context)
        {
            if (context == null)
                return null;
            var httpContextBase = context as HttpContextBase;
            if (httpContextBase != null)
                return httpContextBase;
            var httpContext = context as HttpContext;
            return httpContext == null
                 ? null
                 : new HttpContextWrapper(httpContext);
        }

        static readonly object ContextKey = new object();

        static IServiceProvider GetServiceProvider(HttpContextBase context)
        {
            if (context != null)
            {
                var sp = context.Items[ContextKey] as IServiceProvider;
                if (sp != null)
                    return sp;
            }

            var container = new ServiceContainer(ServiceCenter.Default(context));

            if (context != null)
            {
                var cachedErrorLog = new ErrorLog[1];
                container.AddService(typeof (ErrorLog), delegate
                {
                    return cachedErrorLog[0] ?? (cachedErrorLog[0] =  ErrorLogFactory());
                });

                context.Items[ContextKey] = container;
            }

            return container;
        }

        static Func<ErrorLog> _errorLogFactory;

        static Func<ErrorLog> ErrorLogFactory
        {
            get { return _errorLogFactory ?? (_errorLogFactory = CreateErrorLogFactory()); }
        }

        static Func<ErrorLog> CreateErrorLogFactory()
        {
            string xmlLogPath;
            return ShouldUseErrorLog(config => new SqlErrorLog(config))
                ?? ShouldUseErrorLog(config => new SQLiteErrorLog(config))
                ?? ShouldUseErrorLog(config => new SqlServerCompactErrorLog(config))
                ?? ShouldUseErrorLog(config => new OracleErrorLog(config))
                ?? ShouldUseErrorLog(config => new MySqlErrorLog(config))
                ?? ShouldUseErrorLog(config => new PgsqlErrorLog(config))
                // ReSharper disable once AssignNullToNotNullAttribute
                ?? (Directory.Exists(xmlLogPath = HostingEnvironment.MapPath("~/App_Data/errors/xmlstore"))
                 ? (() => (ErrorLog) new XmlFileErrorLog(xmlLogPath))
                 : new Func<ErrorLog>(() => (ErrorLog) new MemoryErrorLog()));
        }

        static Func<ErrorLog> ShouldUseErrorLog<T>(Func<IDictionary, T> factory) where T : ErrorLog
        {
            var logTypeName = typeof(T).Name;

            const string errorlogSuffix = "ErrorLog";
            if (logTypeName.EndsWith(errorlogSuffix, StringComparison.OrdinalIgnoreCase))
                logTypeName = logTypeName.Substring(0, logTypeName.Length - errorlogSuffix.Length);

            var csName = "elmah:" + logTypeName;
            var css = ConfigurationManager.ConnectionStrings[csName];
            if (css == null || string.IsNullOrEmpty(css.ConnectionString))
                return null;

            var config = new Hashtable
            {
                { "connectionString", css.ConnectionString}
            };

            var appSettings = ConfigurationManager.AppSettings;

            var entries =
                from prefix in new[] { csName + ":" }
                from key in appSettings.AllKeys
                where key.Length > prefix.Length
                   && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                select new
                {
                    Key = key.Substring(prefix.Length),
                    Value = appSettings[key]
                };

            foreach (var e in entries)
                config[e.Key] = e.Value;

            return () =>
            {
                ErrorLog log = factory(/* copy */ new Hashtable(config));
                if (string.IsNullOrEmpty(log.ApplicationName))
                    log.ApplicationName = ApplicationName;
                return log;
            };
        }

        static string _applicationName;

        static string ApplicationName
        {
            get { return _applicationName ?? (_applicationName = GetAppSetting("applicationName")); }
        }

        static string GetAppSetting(string name)
        {
            return ConfigurationManager.AppSettings["elmah:" + name];
        }
    }

    static class WebExtensions
    {
        /// <summary>
        /// Helps with subscribing to <see cref="HttpApplication"/> events
        /// but where the handler
        /// </summary>

        public static void Subscribe(this HttpApplication application,
            Action<EventHandler> subscriber,
            Action<HttpContextBase> handler)
        {
            if (application == null) throw new ArgumentNullException("application");
            if (subscriber == null) throw new ArgumentNullException("subscriber");
            if (handler == null) throw new ArgumentNullException("handler");

            subscriber((sender, _) => handler(new HttpContextWrapper(((HttpApplication)sender).Context)));
        }

        /// <summary>
        /// Same as <see cref="IHttpHandlerFactory.GetHandler"/> except the
        /// HTTP context is typed as <see cref="HttpContextBase"/> instead
        /// of <see cref="HttpContext"/>.
        /// </summary>

        public static IHttpHandler GetHandler(this IHttpHandlerFactory factory,
            HttpContextBase context, string requestType,
            string url, string pathTranslated)
        {
            if (factory == null) throw new ArgumentNullException("factory");
            return factory.GetHandler(context.ApplicationInstance.Context, requestType, url, pathTranslated);
        }
    }

    static class RegexExtensions
    {
        public static T BindNum<T>(this Match match, Func<Group, Group, T> resultor)
        {
            if (match == null) throw new ArgumentNullException("match");
            if (resultor == null) throw new ArgumentNullException("resultor");
            var groups = match.Groups;
            return resultor(groups[1], groups[2]);
        }
    }
}