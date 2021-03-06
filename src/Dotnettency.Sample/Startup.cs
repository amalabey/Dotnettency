﻿using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Dotnettency;
using System;
using System.Text;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Dotnettency.Container;
using Dotnettency.AspNetCore.Modules;
using System.Threading.Tasks;
using Dotnettency.AspNetCore;

namespace Sample
{
    public class Startup
    {
        private readonly IHostingEnvironment _environment;
        private readonly ILoggerFactory _loggerFactory;

        public Startup(IHostingEnvironment environment, ILoggerFactory loggerFactory)
        {
            _environment = environment;
            _loggerFactory = loggerFactory;
        }

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public IServiceProvider ConfigureServices(IServiceCollection services)
        {
            services.AddRouting();

            _loggerFactory.AddConsole();
            var logger = _loggerFactory.CreateLogger<Startup>();

            var serviceProvider = services.AddAspNetCoreMultiTenancy<Tenant>((options) =>
            {
                options                  
                    .InitialiseTenant<TenantShellFactory>() // factory class to load tenant when it needs to be initialised for the first time. Can use overload to provide a delegate instead.                    
                    .ConfigureTenantContainers((containerBuilder) =>
                   {
                       containerBuilder.Events((events) =>
                       {
                           // callback invoked after tenant container is created.
                           events.OnTenantContainerCreated(async (tenantResolver, tenantServiceProvider) =>
                           {
                               var tenant = await tenantResolver;

                           })
                           // callback invoked after a nested container is created for a tenant. i.e typically during a request.
                           .OnNestedTenantContainerCreated(async (tenantResolver, tenantServiceProvider) =>
                           {
                               var tenant = await tenantResolver;

                           });
                       })
                       // Extension methods available here for supported containers. We are using structuremap..
                       // We are using an overload that allows us to configure structuremap with familiar IServiceCollection.
                       .WithStructureMap((tenant, tenantServices) =>
                       {




                           tenantServices.AddSingleton<SomeTenantService>((sp) =>
                           {
                               //var logger = sp.GetRequiredService<ILogger<SomeTenantService>>();
                               logger.LogDebug("Resolving SomeTenantService");
                               return new SomeTenantService(tenant, sp.GetRequiredService<IHostingEnvironment>());
                           });

                           tenantServices.AddModules<ModuleBase>((modules) =>
                           {
                               // Only load these two modules for tenant Bar.
                               if (tenant?.Name == "Bar")
                               {
                                   modules.AddModule<SampleRoutedModule>()
                                          .AddModule<SampleSharedModule>();
                               }

                               modules.ConfigureModules();


                           });
                       })
                       .AddPerRequestContainerMiddlewareServices()
                       .AddPerTenantMiddlewarePipelineServices(); // allows tenants to have there own middleware pipeline accessor stored in their tenant containers.
                       // .WithModuleContainers(); // Creates a child container per IModule.
                   })
                // configure per tenant hosting environment.
                .ConfigurePerTenantHostingEnvironment(_environment, (tenantHostingEnvironmentOptions) =>
                {
                    tenantHostingEnvironmentOptions.OnInitialiseTenantContentRoot((contentRootOptions) =>
                    {
                        // WE use a tenant's guid id to partition one tenants files from another on disk.
                        // NOTE: We use an empty guid for NULL tenants, so that all NULL tenants share the same location.
                        var tenantGuid = (contentRootOptions.Tenant?.TenantGuid).GetValueOrDefault();
                        contentRootOptions.TenantPartitionId(tenantGuid)
                                           .AllowAccessTo(_environment.ContentRootFileProvider); // We allow the tenant content root file provider to access to the environments content root.
                    });

                    tenantHostingEnvironmentOptions.OnInitialiseTenantWebRoot((webRootOptions) =>
                    {
                        // WE use the tenant's guid id to partition one tenants files from another on disk.
                        var tenantGuid = (webRootOptions.Tenant?.TenantGuid).GetValueOrDefault();
                        webRootOptions.TenantPartitionId(tenantGuid)
                                           .AllowAccessTo(_environment.WebRootFileProvider); // We allow the tenant web root file provider to access the environments web root files.
                    });
                });
            });

            // When using tenant containers, must return IServiceProvider.
            return serviceProvider;
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            //  loggerFactory.AddConsole();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouter(((routeBuilder) =>
            {
                // Makes sure that should any child route match, then the tenant container is restored prior to that route handling the request.
                routeBuilder.EnsureTenantContainer<Tenant>((childRouteBuilder) =>
                {
                    // Adds a route that will handle the request via the current tenants middleware pipleine. 
                    childRouteBuilder.MapTenantMiddlewarePipeline<Tenant>((context, appBuilder) =>
                    {

                        var logger = appBuilder.ApplicationServices.GetRequiredService<ILogger<Startup>>();
                        logger.LogDebug("Configuring tenant middleware pipeline for tenant: " + context.Tenant?.Name ?? "");
                        // appBuilder.UseStaticFiles(); // This demonstrates static files middleware, but below I am also using per tenant hosting environment which means each tenant can see its own static files in addition to the main application level static files.

                        appBuilder.UseModules<Tenant, ModuleBase>();

                        // welcome page only enabled for tenant FOO.
                        if (context.Tenant?.Name == "Foo")
                        {
                            appBuilder.UseWelcomePage("/welcome");
                        }
                        // display info.
                        appBuilder.Run(DisplayInfo);

                    }); // handled by the tenant's middleware pipeline - if there is one.                  
                });
            }));

            // This will only be reached if no routes were resolved above.
            app.Run(DisplayInfo);



        }

        public async Task DisplayInfo(HttpContext context)
        {
            var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();
            logger.LogDebug("App Run..");

            var container = context.RequestServices as ITenantContainerAdaptor;
            logger.LogDebug("App Run Container Is: {id}, {containerNAme}, {role}", container.ContainerId, container.ContainerName, container.Role);


            // Use ITenantAccessor to access the current tenant.
            var tenantAccessor = container.GetRequiredService<ITenantAccessor<Tenant>>();
            var tenant = await tenantAccessor.CurrentTenant.Value;

            // This service was registered as singleton in tenant container.
            var someTenantService = container.GetService<SomeTenantService>();

            // The tenant shell to access context for the tenant - even if the tenant is null
            var tenantShellAccessor = context.RequestServices.GetRequiredService<ITenantShellAccessor<Tenant>>();
            var tenantShell = await tenantShellAccessor.CurrentTenantShell.Value;


            string tenantShellId = tenantShell == null ? "{NULL TENANT SHELL}" : tenantShell.Id.ToString();
            string tenantName = tenant == null ? "{NULL TENANT}" : tenant.Name;
            string injectedTenantName = someTenantService?.TenantName ?? "{NULL SERVICE}";

            // Accessing a content file.
            string fileContent = someTenantService?.GetContentFile("/Info.txt");
            context.Response.ContentType = new MediaTypeHeaderValue("application/json").ToString();
            var result = new
            {
                TenantShellId = tenantShellId,
                TenantName = tenantName,
                TenantScopedServiceId = someTenantService?.Id,
                InjectedTenantName = injectedTenantName,
                TenantContentFile = fileContent
            };

            var jsonResult = JsonConvert.SerializeObject(result);
            await context.Response.WriteAsync(jsonResult, Encoding.UTF8);
            logger.LogDebug("App Run Finished..");
        }


    }
}
