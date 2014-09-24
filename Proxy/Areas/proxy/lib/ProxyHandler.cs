using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Routing;
using System.Web.SessionState;


namespace Proxy
{
    public class ProxyHandler : IHttpHandler, IRequiresSessionState
    {
        public bool IsReusable { get { return true; } }

        public void ProcessRequest(HttpContext context)
        {
            ProxyServer server = new ProxyServer(context);
            if (string.IsNullOrEmpty(server.Url)) return;

            HttpWebRequest request = server.GetRequest();

            HttpWebResponse response = server.GetResponse(request);
            if (response == null) return;

            byte[] responseData = server.GetResponseStreamBytes(response);
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentType = response.ContentType;
            if (response.ContentType.StartsWith("text/html"))
            {
                string html = server.UpdateHTML(responseData);
                context.Response.Write(html);
            }
            else
            {
                context.Response.OutputStream.Write(responseData, 0, responseData.Length);
            }

            server.SetContextCookies(response);

            response.Close();
            context.Response.End();
        }

    }


    internal class ProxyServer
    {
        public const string QS_CONFIG = "config"; //{ type:"GET" }
        public const string QS_URL = "url";
        public const string UP_TOKEN = "token";

        public string Url { get; set; }
        public string UrlReferrer { get; set; }
        HttpContext _context;

        public ProxyServer(HttpContext context)
        {
            _context = context;

            string proxyUrl = context.Request.Url.ToString();

            Url = GetRequestUrl(proxyUrl);
            if (context.Request.UrlReferrer != null)
            {
                UrlReferrer = GetRequestUrl(context.Request.UrlReferrer.ToString());
            }
        }
        public HttpWebRequest GetRequest()
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(Url);
                //if (ConfigurationManager.AppSettings["UpchainProxy"] == "true")
                //    ProxyRequest.Proxy = new WebProxy(ConfigurationManager.AppSettings["Proxy"], true);

                request.Method = _context.Request.HttpMethod;
                request.UserAgent = _context.Request.UserAgent;
                request.Referer = UrlReferrer;
                request.KeepAlive = true;
                request.CookieContainer = new CookieContainer();
                for (int i = 0; i < _context.Request.Cookies.Count; i++)
                {
                    HttpCookie navigatorCookie = _context.Request.Cookies[i];
                    Cookie c = new Cookie(navigatorCookie.Name, navigatorCookie.Value);
                    c.Domain = new Uri(Url).Host;
                    c.Expires = navigatorCookie.Expires;
                    c.HttpOnly = navigatorCookie.HttpOnly;
                    c.Path = navigatorCookie.Path;
                    c.Secure = navigatorCookie.Secure;
                    request.CookieContainer.Add(c);
                }
                if (request.Method == "POST")
                {
                    Stream clientStream = _context.Request.InputStream;
                    byte[] clientPostData = new byte[_context.Request.InputStream.Length];
                    clientStream.Read(clientPostData, 0,
                                     (int)_context.Request.InputStream.Length);

                    request.ContentType = _context.Request.ContentType;
                    request.ContentLength = clientPostData.Length;
                    Stream stream = request.GetRequestStream();
                    stream.Write(clientPostData, 0, clientPostData.Length);
                    stream.Close();
                }
                return request;
            }
            catch (Exception e)
            {
                return null;
            }
        }

        public HttpWebResponse GetResponse(HttpWebRequest request)
        {
            if (request == null) return null;
            HttpWebResponse response;

            try
            {
                System.Net.ServicePointManager.Expect100Continue = false;
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (System.Net.WebException)
            {
                // Send 404 to client 
                _context.Response.StatusCode = 404;
                _context.Response.StatusDescription = "Page Not Found";
                _context.Response.Write("Page not found");
                _context.Response.End();
                return null;
            }

            return response;
        }

        public byte[] GetResponseStreamBytes(HttpWebResponse response)
        {
            if (response == null) return null;
            int bufferSize = 256;
            byte[] buffer = new byte[bufferSize];
            Stream responseStream;
            MemoryStream memoryStream = new MemoryStream();
            int remoteResponseCount;
            byte[] responseData;

            responseStream = response.GetResponseStream();
            remoteResponseCount = responseStream.Read(buffer, 0, bufferSize);

            while (remoteResponseCount > 0)
            {
                memoryStream.Write(buffer, 0, remoteResponseCount);
                remoteResponseCount = responseStream.Read(buffer, 0, bufferSize);
            }

            responseData = memoryStream.ToArray();

            memoryStream.Close();
            responseStream.Close();

            memoryStream.Dispose();
            responseStream.Dispose();

            return responseData;
        }

        public void SetContextCookies(HttpWebResponse response)
        {
            _context.Response.Cookies.Clear();

            foreach (Cookie receivedCookie in response.Cookies)
            {
                HttpCookie c = new HttpCookie(receivedCookie.Name,
                                   receivedCookie.Value);
                c.Domain = _context.Request.Url.Host;
                c.Expires = receivedCookie.Expires;
                c.HttpOnly = receivedCookie.HttpOnly;
                c.Path = receivedCookie.Path;
                c.Secure = receivedCookie.Secure;
                _context.Response.Cookies.Add(c);
            }
        }
        public string GetRequestUrl(string proxyUrl)
        {
            string url = proxyUrl;
            int index = url.IndexOf(QS_URL + "=");
            if (index < 0)
            {
                Uri uri = new Uri(proxyUrl);
                index = Array.IndexOf(uri.Segments, UP_TOKEN + "/");
                if (index < 0) return null;
                string token = uri.Segments[index + 1];
                url = DecodeUrl(token);
                url += uri.Query;
            }
            else
            {
                url = url.Substring(index + QS_URL.Length + 1);
            }
            return url;
        }
        public string UpdateHTML(byte[] responseData)
        {
            string html = Encoding.ASCII.GetString(responseData);
            string baseUrl = Url;
            HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            
            
            
            HtmlNodeCollection links = doc.DocumentNode.SelectNodes("//*[@src or @href or @action or @background or @lowsrc]");
            if (links == null) return html;
            Uri baseUri = new Uri(Url);
            foreach (HtmlNode link in links)
            {
                if (link.Attributes["src"] != null)
                    link.Attributes["src"].Value = ToAbsoluteProxy(link.Attributes["src"].Value, baseUri);
                else if (link.Attributes["href"] != null)
                    link.Attributes["href"].Value = ToAbsoluteProxy(link.Attributes["href"].Value, baseUri);
                else if (link.Attributes["action"] != null)
                    link.Attributes["action"].Value = ToAbsoluteProxy(link.Attributes["action"].Value, baseUri, true);
                else if (link.Attributes["background"] != null)
                    link.Attributes["background"].Value = ToAbsoluteProxy(link.Attributes["background"].Value, baseUri);
                else if (link.Attributes["lowsrc"] != null)
                    link.Attributes["lowsrc"].Value = ToAbsoluteProxy(link.Attributes["lowsrc"].Value, baseUri);
            }

            StringWriter writer = new StringWriter();
            bool IsWithTopBar = true;
            if (IsWithTopBar)
            {
                // added to enable loading into same page instead of iframe 
                var body = doc.DocumentNode.SelectSingleNode("//body").InnerHtml;
                var head = doc.DocumentNode.SelectSingleNode("//head");

                HtmlAgilityPack.HtmlDocument docProxy = new HtmlAgilityPack.HtmlDocument();
                docProxy.LoadHtml(GetProxyHtml());
                var headProxy = docProxy.DocumentNode.SelectSingleNode("//head");
                //headProxy.InnerHtml = head;
                headProxy.AppendChildren(head.ChildNodes);

                var proxyContainerUrl = docProxy.DocumentNode.SelectSingleNode("//*[@id='proxy-container-url']");
                proxyContainerUrl.Attributes["value"].Value = Url;
                var bodyProxy = docProxy.DocumentNode.SelectSingleNode("//div[@id='proxy-container-content']");
                bodyProxy.InnerHtml = body;
                //HtmlNodeCollection links = bodyProxy.SelectNodes("//*[@src or @href or @action or @background or @lowsrc]"); 

                var doctype = doc.DocumentNode.SelectSingleNode("/comment()[starts-with(.,'<!DOCTYPE')]");
                var doctypeProxy = docProxy.DocumentNode.SelectSingleNode("/comment()[starts-with(.,'<!DOCTYPE')]");
                if (doctype != null) doctypeProxy.InnerHtml = doctype.OuterHtml;
                else doctypeProxy.InnerHtml = null;


                docProxy.Save(writer);
            }
            else
            {
                doc.Save(writer);
            }
            string newHtml = writer.ToString();
            newHtml = UpdateUrl(newHtml, baseUri);
            return newHtml;
        }
        private string UpdateUrl(string html, Uri baseUri)
        {
            string rx_preurl = "preurl", rx_url = "url", rx_posturl = "posturl";
            var newhtml = Regex.Replace(html, @"(?<preurl>url\s*\(['""]?)(?<url>.*?)(?<posturl>['""]?\))", delegate(Match match)
            {
                string url = match.Groups[rx_url].Value;
                if (!(url.IndexOf(":") >= 0 || url.IndexOf("+") >= 0 || url.IndexOf(";") >= 0))
                    url = ToAbsoluteProxy(url, baseUri);
                return match.Groups[rx_preurl].Value + url + match.Groups[rx_posturl].Value;
            });
            return newhtml;
        }
        private string ToAbsoluteProxy(string relativeUrl, Uri baseUri, bool IsPath = false)
        {
            var requestUrl = ToAbsolute(relativeUrl, baseUri);
            var url = _context.Request.Url.GetLeftPart(UriPartial.Path);
            var index = url.IndexOf("/" + UP_TOKEN + "/");
            if (index > 0)
            {
                url = url.Substring(0, index);
            }
            if (IsPath)
            {
                url += "/" + UP_TOKEN + "/" + EncodeUrl(requestUrl);
            }
            else
            {
                url += "?" + QS_URL + "=" + requestUrl;
            }
            return url;
        }
        private string ToAbsolute(string relativeUrl, Uri baseUri)
        {
            if (relativeUrl.StartsWith("http")) return relativeUrl;
            Uri uri = new Uri(baseUri, relativeUrl);
            string url = uri.ToString();
            return url;
        }
        public string EncodeUrl(string url)
        {
            return HttpServerUtility.UrlTokenEncode(Encoding.UTF8.GetBytes(url));
        }
        public string DecodeUrl(string url)
        {
            return Encoding.UTF8.GetString(HttpServerUtility.UrlTokenDecode(url));
        }
        public string GetProxyHtml()
        {
            string html = @"<!DOCTYPE html>
<html>
<head>
    <meta charset='utf-8' />
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <style type='text/css'>
        body{
            margin:0px;
            padding:0px;
        }
        #proxy-container-spacer {
            height: 50px;
        }

        #proxy-container-top {
            width: 100%;
            position: fixed;
            display: block;
            z-index: 2147483638;
            top: 0 !important;
            margin: auto;
            padding: 10px 6%;
            background-color: rgb(163, 27, 27);
        }

            #proxy-container-top h1 {
                margin: 0px;
                width: 10%;
                float: left;
                font-family: 'Curlz MT';
                text-align: center;
            }

        #proxy-container-url {
            width: 80%;
            border: 1px solid #c4c4c4;
            height: 25px;
            font-size: 13px;
            border-radius: 4px;
            -moz-border-radius: 4px;
            -webkit-border-radius: 4px;
            box-shadow: 0px 0px 8px #d9d9d9;
            -moz-box-shadow: 0px 0px 8px #d9d9d9;
            -webkit-box-shadow: 0px 0px 8px #d9d9d9;
            padding: 2px 10px;
        }

            #proxy-container-url:focus {
                outline: none;
                border: 1px solid #7bc1f7;
                box-shadow: 0px 0px 8px #7bc1f7;
                -moz-box-shadow: 0px 0px 8px #7bc1f7;
                -webkit-box-shadow: 0px 0px 8px #7bc1f7;
            }

        #proxy-container-content {
            position: relative;
            display: block;
        }

        #proxy-container-go-button {
            -moz-box-shadow: 0px 10px 14px -7px #3e7327;
            -webkit-box-shadow: 0px 10px 14px -7px #3e7327;
            box-shadow: 0px 10px 14px -7px #3e7327;
            background: -webkit-gradient(linear, left top, left bottom, color-stop(0.05, #77b55a), color-stop(1, #72b352));
            background: -moz-linear-gradient(top, #77b55a 5%, #72b352 100%);
            background: -webkit-linear-gradient(top, #77b55a 5%, #72b352 100%);
            background: -o-linear-gradient(top, #77b55a 5%, #72b352 100%);
            background: -ms-linear-gradient(top, #77b55a 5%, #72b352 100%);
            background: linear-gradient(to bottom, #77b55a 5%, #72b352 100%);
            filter: progid:DXImageTransform.Microsoft.gradient(startColorstr='#77b55a', endColorstr='#72b352',GradientType=0);
            background-color: #77b55a;
            -moz-border-radius: 4px;
            -webkit-border-radius: 4px;
            border-radius: 4px;
            border: 1px solid #4b8f29;
            display: inline-block;
            cursor: pointer;
            color: #ffffff;
            font-family: arial;
            font-weight: bold;
            padding: 6px 12px;
            text-decoration: none;
            text-shadow: 0px 1px 0px #5b8a3c;
            width: 95px;
            font-size: 13px;
        }

            #proxy-container-go-button :hover {
                background: -webkit-gradient(linear, left top, left bottom, color-stop(0.05, #72b352), color-stop(1, #77b55a));
                background: -moz-linear-gradient(top, #72b352 5%, #77b55a 100%);
                background: -webkit-linear-gradient(top, #72b352 5%, #77b55a 100%);
                background: -o-linear-gradient(top, #72b352 5%, #77b55a 100%);
                background: -ms-linear-gradient(top, #72b352 5%, #77b55a 100%);
                background: linear-gradient(to bottom, #72b352 5%, #77b55a 100%);
                filter: progid:DXImageTransform.Microsoft.gradient(startColorstr='#72b352', endColorstr='#77b55a',GradientType=0);
                background-color: #72b352;
            }

            #proxy-container-go-button :active {
                position: relative;
                top: 1px;
            }
    </style>
    <script type='text/javascript'>
        function domReady(callback) {
            arrDomReadyCallBacks.push(callback);
            /* Mozilla, Chrome, Opera */
            var browserTypeSet = false;
            if (document.addEventListener) {
                browserTypeSet = true;
                document.addEventListener('DOMContentLoaded', excuteDomReadyCallBacks, false);
            }
            /* Safari, iCab, Konqueror */
            if (/KHTML|WebKit|iCab/i.test(navigator.userAgent) && !browserTypeSet) {
                browserTypeSet = true;
                var DOMLoadTimer = setInterval(function () {
                    if (/loaded|complete/i.test(document.readyState)) {
                        //callback();
                        excuteDomReadyCallBacks();
                        clearInterval(DOMLoadTimer);
                    }
                }, 10);
            }
            /* Other web browsers */
            if (!browserTypeSet) {
                window.onload = excuteDomReadyCallBacks;
            }
        }
        var arrDomReadyCallBacks = [];
        function excuteDomReadyCallBacks() {
            for (var i = 0; i < arrDomReadyCallBacks.length; i++) {
                arrDomReadyCallBacks[i]();
            }
            arrDomReadyCallBacks = [];
        }
        function addEvent(obj, type, fn) {
            if (obj.addEventListener)
                obj.addEventListener(type, fn, false);
            else if (obj.attachEvent)
                obj.attachEvent('on' + type, function () { return fn.apply(obj, [window.event]); });
        }
        function ready() {
            addEvent(document.getElementById('proxy-container-go-button'), 'click', function () {
                var url = document.getElementById('proxy-container-url').value;
                document.location.href = '/hidemyip.proxy?url=' + url;
            });
            addEvent(document.getElementById('proxy-container-url'), 'keypress', function () {
                if (event.keyCode == 13) document.getElementById('proxy-container-go-button').click();
            });
        }
        domReady(ready);
    </script>
</head>
<body>
    <div id='proxy-container-spacer'></div>
    <div id='proxy-container-top'>
        <input type='text' id='proxy-container-url' value='http://www.bing.com' />
        <button type='button' id='proxy-container-go-button'>Hide My IP</button>
    </div>
    <div id='proxy-container-content'></div>
</body>
</html>";
            return html;
        }
    }
    public class UrlReferrerCheck : IRouteConstraint
    {
        public UrlReferrerCheck() { }

        public bool Match(HttpContextBase context, Route route, string parameterName, RouteValueDictionary values, RouteDirection routeDirection)
        {
            if (context != null && context.Request != null && context.Request.UrlReferrer != null)
            {
                return context.Request.UrlReferrer.ToString().Contains(".proxy?")
                    && !context.Request.Url.ToString().Contains(".proxy?");
            }
            return false;
        }
    }
}


