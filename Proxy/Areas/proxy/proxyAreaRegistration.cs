using System.Web.Mvc;
using System.Web.Routing;

namespace Proxy.Areas.proxy
{
    public class proxyAreaRegistration : AreaRegistration
    {
        public override string AreaName
        {
            get
            {
                return "proxy";
            }
        }

        public override void RegisterArea(AreaRegistrationContext context)
        {
            RouteTable.Routes.IgnoreRoute("{resource}.proxy/{*pathInfo}");

            context.MapRoute(
                "UrlReferrerCheck", // Route name
                "{*url}", // URL with parameters
                new { controller = "hidemyip", action = "UrlRedirect", id = UrlParameter.Optional }, // Parameter default
                new { controller = new UrlReferrerCheck() }  // our constraint
            ); 
            
            context.MapRoute(
                "proxy_default",
                "proxy/{controller}/{action}/{id}",
                new { action = "Index", id = UrlParameter.Optional }
            );

            
        }
    }
}