using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using OrchardCore.Admin;
using OrchardCore.ContentManagement.Display;
using OrchardCore.DisplayManagement.Layout;
using OrchardCore.DisplayManagement.ModelBinding;
using OrchardCore.DisplayManagement.Shapes;
using OrchardCore.DisplayManagement.Theming;
using OrchardCore.DisplayManagement.Zones;
using OrchardCore.Environment.Cache;
using OrchardCore.Environment.Shell.Scope;
using OrchardCore.Layers.Handlers;
using OrchardCore.Layers.ViewModels;
using OrchardCore.Mvc.Utilities;
using OrchardCore.Scripting;

namespace OrchardCore.Layers.Services
{
    public class LayerFilter : IAsyncResultFilter
    {
        private readonly IScriptingManager _scriptingManager;
        private readonly IMemoryCache _memoryCache;
        private readonly ISignal _signal;

        private IThemeManager _themeManager;
        private IAdminThemeService _adminThemeService;
        private ILayerService _layerService;
        private ILayoutAccessor _layoutAccessor;
        private IContentItemDisplayManager _contentItemDisplayManager;
        private IUpdateModel _updater;

        public LayerFilter(
            IScriptingManager scriptingManager,
            IMemoryCache memoryCache,
            ISignal signal)
        {
            _scriptingManager = scriptingManager;
            _memoryCache = memoryCache;
            _signal = signal;
        }

        public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
        {
            // Should only run on the front-end for a full view
            if ((context.Result is ViewResult || context.Result is PageResult) &&
                !AdminAttribute.IsApplied(context.HttpContext))
            {
                // Even if the Admin attribute is not applied we might be using the admin theme, for instance in Login views.
                // In this case don't render Layers.

                // Resolve scoped services lazy if we got this far.
                _themeManager ??= context.HttpContext.RequestServices.GetRequiredService<IThemeManager>();
                _adminThemeService ??= context.HttpContext.RequestServices.GetRequiredService<IAdminThemeService>();

                var selectedTheme = (await _themeManager.GetThemeAsync())?.Id;
                var adminTheme = await _adminThemeService.GetAdminThemeNameAsync();
                if (selectedTheme == adminTheme)
                {
                    await next.Invoke();
                    return;
                }

                // Resolve scoped services lazy if we got this far.
                _layerService ??= context.HttpContext.RequestServices.GetRequiredService<ILayerService>();
                _layoutAccessor ??= context.HttpContext.RequestServices.GetRequiredService<ILayoutAccessor>();
                _contentItemDisplayManager ??= context.HttpContext.RequestServices.GetRequiredService<IContentItemDisplayManager>();
                _updater ??= context.HttpContext.RequestServices.GetRequiredService<IUpdateModelAccessor>().ModelUpdater;

                var widgets = await _memoryCache.GetOrCreateAsync("OrchardCore.Layers.LayerFilter:AllWidgets", entry =>
                {
                    entry.AddExpirationToken(_signal.GetToken(LayerMetadataHandler.LayerChangeToken));
                    return _layerService.GetLayerWidgetsMetadataAsync(x => x.Published);
                });

                var layers = (await _layerService.GetLayersAsync()).Layers.ToDictionary(x => x.Name);

                dynamic layout = await _layoutAccessor.GetLayoutAsync();

                var engine = _scriptingManager.GetScriptingEngine("js");
                var scope = engine.CreateScope(_scriptingManager.GlobalMethodProviders.SelectMany(x => x.GetMethods()), ShellScope.Services, null, null);

                var layersCache = new Dictionary<string, bool>();

                foreach (var widget in widgets)
                {
                    var layer = layers[widget.Layer];

                    if (layer == null)
                    {
                        continue;
                    }

                    bool display;
                    if (!layersCache.TryGetValue(layer.Name, out display))
                    {
                        if (String.IsNullOrEmpty(layer.Rule))
                        {
                            display = false;
                        }
                        else
                        {
                            display = Convert.ToBoolean(engine.Evaluate(scope, layer.Rule));
                        }

                        layersCache[layer.Rule] = display;
                    }

                    if (!display)
                    {
                        continue;
                    }

                    var widgetContent = await _contentItemDisplayManager.BuildDisplayAsync(widget.ContentItem, _updater);

                    widgetContent.Classes.Add("widget");
                    widgetContent.Classes.Add("widget-" + widget.ContentItem.ContentType.HtmlClassify());

                    var wrapper = new WidgetWrapper
                    {
                        Widget = widget.ContentItem,
                        Content = widgetContent
                    };

                    wrapper.Metadata.Alternates.Add("Widget_Wrapper__" + widget.ContentItem.ContentType);
                    wrapper.Metadata.Alternates.Add("Widget_Wrapper__Zone__" + widget.Zone);

                    var contentZone = layout.Zones[widget.Zone];

                    if (contentZone is ZoneOnDemand zoneOnDemand)
                    {
                        await zoneOnDemand.AddAsync(wrapper);
                    }
                    else if (contentZone is Shape shape)
                    {
                        shape.Add(wrapper);
                    }
                }
            }

            await next.Invoke();
        }
    }
}
