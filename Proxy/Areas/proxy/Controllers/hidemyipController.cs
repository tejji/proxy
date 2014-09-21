using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Mvc;

namespace Proxy.Areas.proxy.Controllers
{
    public class hidemyipController : Controller
    {
        // GET: proxy/hidemyip
        public ActionResult Index()
        {
            return View();
        }
        public ActionResult UrlRedirect()
        {
            var qs = HttpUtility.ParseQueryString(Request.UrlReferrer.Query);
            string baseurl = qs[ProxyServer.QS_URL];
            string relativeUrl = Request.Url.PathAndQuery;
            Uri uri = new Uri(new Uri(baseurl), relativeUrl);
            string url = uri.ToString();
            url = Request.UrlReferrer.GetLeftPart(UriPartial.Path) + "?" + ProxyServer.QS_URL + "=" + url;
            Response.Redirect(url, false);
            return null;
        }
    }
}