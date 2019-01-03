// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Razor.Compilation;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure
{
    public class PageActionDescriptorProvider : IActionDescriptorProvider
    {
        private readonly ApplicationPartManager _partManager;
        private readonly IPageRouteModelProvider[] _routeModelProviders;
        private readonly IPageRouteModelConvention[] _conventions;

        [Obsolete(
            "This constructor is obsolete and code using it may not behave correctly. " +
            "Use the constructor that accepts an ApplicationPartManager.")]
        public PageActionDescriptorProvider(
            IEnumerable<IPageRouteModelProvider> pageRouteModelProviders,
            IOptions<MvcOptions> mvcOptionsAccessor, // Unused but left here to avoid a breaking change
            IOptions<RazorPagesOptions> pagesOptionsAccessor)
        {
            _routeModelProviders = pageRouteModelProviders.OrderBy(p => p.Order).ToArray();

            _conventions = pagesOptionsAccessor.Value.Conventions
                .OfType<IPageRouteModelConvention>()
                .ToArray();
        }

        public PageActionDescriptorProvider(
            ApplicationPartManager partManager,
            IEnumerable<IPageRouteModelProvider> pageRouteModelProviders,
            IOptions<RazorPagesOptions> pagesOptionsAccessor)
        {
            if (partManager == null)
            {
                throw new ArgumentNullException(nameof(partManager));
            }

            if (pageRouteModelProviders == null)
            {
                throw new ArgumentNullException(nameof(pageRouteModelProviders));
            }

            if (pagesOptionsAccessor == null)
            {
                throw new ArgumentNullException(nameof(pagesOptionsAccessor));
            }

            _partManager = partManager;

            _routeModelProviders = pageRouteModelProviders.OrderBy(p => p.Order).ToArray();

            _conventions = pagesOptionsAccessor.Value.Conventions
                .OfType<IPageRouteModelConvention>()
                .ToArray();
        }

        public int Order { get; set; } = -900; // Run after the default MVC provider, but before others.

        public void OnProvidersExecuting(ActionDescriptorProviderContext context)
        {
            var pageRouteModels = BuildModel();

            for (var i = 0; i < pageRouteModels.Count; i++)
            {
                AddActionDescriptors(context.Results, pageRouteModels[i]);
            }
        }

        protected IList<PageRouteModel> BuildModel()
        {
            var viewsFeature = new ViewsFeature();
            _partManager.PopulateFeature(viewsFeature);

            var descriptors = new List<RazorCompiledItem>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var viewDescriptor in viewsFeature.ViewDescriptors)
            {
                if (!visited.Add(viewDescriptor.RelativePath))
                {
                    // Already seen an descriptor with a higher "order"
                    continue;
                }

                descriptors.Add(viewDescriptor.Item);
            }

            var context = new PageRouteModelProviderContext(descriptors);

            for (var i = 0; i < _routeModelProviders.Length; i++)
            {
                _routeModelProviders[i].OnProvidersExecuting(context);
            }

            for (var i = _routeModelProviders.Length - 1; i >= 0; i--)
            {
                _routeModelProviders[i].OnProvidersExecuted(context);
            }

            return context.RouteModels;
        }

        public void OnProvidersExecuted(ActionDescriptorProviderContext context)
        {
        }

        private void AddActionDescriptors(IList<ActionDescriptor> actions, PageRouteModel model)
        {
            for (var i = 0; i < _conventions.Length; i++)
            {
                _conventions[i].Apply(model);
            }

            foreach (var selector in model.Selectors)
            {
                var descriptor = new PageActionDescriptor
                {
                    ActionConstraints = selector.ActionConstraints.ToList(),
                    AreaName = model.AreaName,
                    AttributeRouteInfo = new AttributeRouteInfo
                    {
                        Name = selector.AttributeRouteModel.Name,
                        Order = selector.AttributeRouteModel.Order ?? 0,
                        Template = TransformPageRoute(model, selector),
                        SuppressLinkGeneration = selector.AttributeRouteModel.SuppressLinkGeneration,
                        SuppressPathMatching = selector.AttributeRouteModel.SuppressPathMatching,
                    },
                    DisplayName = $"Page: {model.ViewEnginePath}",
                    EndpointMetadata = selector.EndpointMetadata.ToList(),
                    FilterDescriptors = Array.Empty<FilterDescriptor>(),
                    Properties = new Dictionary<object, object>(model.Properties),
                    RelativePath = model.RelativePath,
                    ViewEnginePath = model.ViewEnginePath,
                };

                foreach (var kvp in model.RouteValues)
                {
                    if (!descriptor.RouteValues.ContainsKey(kvp.Key))
                    {
                        descriptor.RouteValues.Add(kvp.Key, kvp.Value);
                    }
                }

                if (!descriptor.RouteValues.ContainsKey("page"))
                {
                    descriptor.RouteValues.Add("page", model.ViewEnginePath);
                }

                actions.Add(descriptor);
            }
        }

        private static string TransformPageRoute(PageRouteModel model, SelectorModel selectorModel)
        {
            // Transformer not set on page route
            if (model.RouteParameterTransformer == null)
            {
                return selectorModel.AttributeRouteModel.Template;
            }

            var pageRouteMetadata = selectorModel.EndpointMetadata.OfType<PageRouteMetadata>().SingleOrDefault();
            if (pageRouteMetadata == null)
            {
                // Selector does not have expected metadata
                // This selector was likely configured by AddPageRouteModelConvention
                // Use the existing explicitly configured template
                return selectorModel.AttributeRouteModel.Template;
            }

            var segments = pageRouteMetadata.PageRoute.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = model.RouteParameterTransformer.TransformOutbound(segments[i]);
            }

            var transformedPageRoute = string.Join("/", segments);

            // Combine transformed page route with template
            return AttributeRouteModel.CombineTemplates(transformedPageRoute, pageRouteMetadata.RouteTemplate);
        }
    }
}