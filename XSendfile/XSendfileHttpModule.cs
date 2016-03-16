using System;
using System.Linq;
using System.Web;
using System.Configuration;
using System.IO;
using System.Text.RegularExpressions;

//
// This feels hacky
//
using Microsoft.Web.Administration;

namespace XSendFile
{
    public class XSendFileHttpModule : IHttpModule
    {
        public void Init(HttpApplication context)
        {
            context.EndRequest += context_EndRequest;
        }

        private static void context_EndRequest(object sender, EventArgs e)
        {
            var response = HttpContext.Current.Response;
            var request = HttpContext.Current.Request;

            //
            // Check for the X-Send headers
            //
            var filePath = response.Headers.Get("X-Sendfile") ?? response.Headers.Get("X-Accel-Redirect");

            if (filePath == null) return;
            //
            // Determine the file path and ready the response
            //
            //if (ConfigurationManager.AppSettings["XSendDir"] != null)
            //    filePath = Path.Combine(ConfigurationManager.AppSettings["XSendDir"], filePath);    // if there is a base path set (file will be located above this)
            //else if (ConfigurationManager.AppSettings["XAccelLocation"] != null)
            //    filePath = filePath.Replace(ConfigurationManager.AppSettings["XAccelLocation"], ConfigurationManager.AppSettings["XAccelRoot"]);

            response.Clear();                               // Clears output buffer
            response.Headers.Remove("X-Sendfile");          // Remove unwanted headers
            response.Headers.Remove("X-Accel-Redirect");
            filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filePath);

            //
            // Set the cache policy
            //
            switch (ConfigurationManager.AppSettings["XSendCache"])
            {
                case null:
                    response.Cache.SetCacheability(HttpCacheability.NoCache);
                    break;
                case "Public":
                    response.Cache.SetCacheability(HttpCacheability.Public);
                    break;
                default:
                    response.Cache.SetCacheability(HttpCacheability.Private);
                    break;
            }


            //
            // Get the file information and set headers appropriately
            //
            var file = new FileInfo(filePath);

            if (!file.Exists)
            {
                RedirectOnNotFound(HttpContext.Current);
                return;
            }

            if (filePath[filePath.Length - 1] == '.')
            {
                RedirectOnNotFound(HttpContext.Current);
                return;
            }

            response.Cache.SetLastModified(file.LastWriteTimeUtc);
            response.Headers.Remove("Content-Length");
            response.Headers.Remove("X-E107-Redirect-To");

            var rex = new Regex(@"^bytes=\d*-\d*(,\d*-\d*)*$");
            if (!string.IsNullOrEmpty(request.ServerVariables["HTTP_RANGE"]) && rex.IsMatch(request.ServerVariables["HTTP_RANGE"]))
            {
                //request for chunk
                RangeDownload(file.FullName, HttpContext.Current);
            }
            else
            {
                response.AddHeader("Content-Length", file.Length.ToString());
                response.AppendHeader("Accept-Ranges", "bytes");

                //
                // Check if we want to detect the mime type of the current content
                //
                if (ConfigurationManager.AppSettings["XSendMime"] == null)
                {
                    var staticContentSection = WebConfigurationManager.GetSection(HttpContext.Current, "system.webServer/staticContent");
                    var staticContentCollection = staticContentSection.GetCollection();

                    var mt =
                        staticContentCollection.FirstOrDefault(
                            a =>
                                string.Equals(a.Attributes["fileExtension"].Value.ToString(), file.Extension,
                                    StringComparison.CurrentCultureIgnoreCase));

                    response.ContentType = mt != null ? mt.GetAttributeValue("mimeType").ToString() : "application/octet-stream";
                }

                //
                // Set a content disposition if it is not already set by the application
                //
                if (response.Headers["Content-Disposition"] == null)
                    response.AppendHeader("Content-Disposition", string.Format("attachment;filename=\"{0}\"", file.Name));

                //
                //  Send the file without loading it into memory
                //
                response.TransmitFile(file.FullName);
            }
        }

        public void Dispose() { }

        private static void RedirectOnNotFound(HttpContext context)
        {
            var response = context.Response;
            var redirectTo = response.Headers.Get("X-E107-Redirect-To");
            if (redirectTo != null)
            {
                response.Headers.Remove("X-E107-Redirect-To");
                response.Redirect(redirectTo);
            }
            else
            {
                throw new HttpException(404, "File_does_not_exist");
            }
        }

        //
        // http://blogs.visigo.com/chriscoulson/easy-handling-of-http-range-requests-in-asp-net/
        //
        private static void RangeDownload(string fullpath, HttpContext context)
        {
            long size, start, end, length, fp = 0;

            using (var reader = new StreamReader(fullpath))
            {

                size = reader.BaseStream.Length;
                start = 0;
                end = size - 1;
                // Now that we've gotten so far without errors we send the accept range header
                /* At the moment we only support single ranges.
                 * Multiple ranges requires some more work to ensure it works correctly
                 * and comply with the spesifications: http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2
                 *
                 * Multirange support annouces itself with:
                 * header('Accept-Ranges: bytes');
                 *
                 * Multirange content must be sent with multipart/byteranges mediatype,
                 * (mediatype = mimetype)
                 * as well as a boundry header to indicate the various chunks of data.
                 */
                context.Response.AddHeader("Accept-Ranges", "0-" + size);

                var rex = new Regex(@"^bytes=\d*-\d*(,\d*-\d*)*$");
                if (!rex.IsMatch(context.Request.ServerVariables["HTTP_RANGE"]))
                {
                    context.Response.AddHeader("Content-Range", string.Format("bytes {0}-{1}/{2}", start, end, size));
                    throw new HttpException(416, "Requested Range Not Satisfiable");
                }

                // header('Accept-Ranges: bytes');
                // multipart/byteranges
                // http://www.w3.org/Protocols/rfc2616/rfc2616-sec19.html#sec19.2         
                long anotherStart;
                var anotherEnd = end;
                var arrSplit = context.Request.ServerVariables["HTTP_RANGE"].Split(new char[] { Convert.ToChar("=") });
                var range = arrSplit[1];

                // Make sure the client hasn't sent us a multibyte range
                if (range.IndexOf(",", StringComparison.Ordinal) > -1)
                {
                    context.Response.AddHeader("Content-Range", string.Format("bytes {0}-{1}/{2}", start, end, size));
                    throw new HttpException(416, "Requested Range Not Satisfiable");
                }

                // If the range starts with an '-' we start from the beginning
                // If not, we forward the file pointer
                // And make sure to get the end byte if spesified
                if (range.StartsWith("-"))
                {
                    // The n-number of the last bytes is requested
                    anotherStart = size - Convert.ToInt64(range.Substring(1));
                }
                else
                {
                    arrSplit = range.Split(new char[] { Convert.ToChar("-") });
                    anotherStart = Convert.ToInt64(arrSplit[0]);
                    long temp = 0;
                    anotherEnd = (arrSplit.Length > 1 && long.TryParse(arrSplit[1], out temp)) ? Convert.ToInt64(arrSplit[1]) : size;
                }
                /* Check the range and make sure it's treated according to the specs.
                 * http://www.w3.org/Protocols/rfc2616/rfc2616-sec14.html
                 */
                // End bytes can not be larger than $end.
                anotherEnd = (anotherEnd > end) ? end : anotherEnd;
                // Validate the requested range and return an error if it's not correct.
                if (anotherStart > anotherEnd || anotherStart > size - 1 || anotherEnd >= size)
                {
                    context.Response.AddHeader("Content-Range", string.Format("bytes {0}-{1}/{2}", start, end, size));
                    throw new HttpException(416, "Requested Range Not Satisfiable");
                }
                start = anotherStart;
                end = anotherEnd;

                length = end - start + 1; // Calculate new content length
                fp = reader.BaseStream.Seek(start, SeekOrigin.Begin);
                context.Response.StatusCode = 206;
            }

            // Notify the client the byte range we'll be outputting
            context.Response.AddHeader("Content-Range", string.Format("bytes {0}-{1}/{2}", start, end, size));
            context.Response.AddHeader("Content-Length", length.ToString());
            // Start buffered download

            // Don't buffer output as the file might be very large
            context.Response.BufferOutput = false;
            context.Response.WriteFile(fullpath, fp, length);
            context.Response.End();
        }

    }
}
