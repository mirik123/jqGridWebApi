/**
 * jqGrid server-side extension for Web Api
 * Copyright (c) 2014-2015, Mark Babayev
 * MIT license:
 * http://www.opensource.org/licenses/mit-license.php
**/ 

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Filters;
using System.Web.Http.ModelBinding;

namespace jqGridExtension
{
    public class jqGridHelper
    {
        public static GridModel ApplyJqGridFilters<T>(IQueryable<T> model, GridSettings grid, dynamic userdata = null) where T: class
        {
            //filtering
            if (grid.IsSearch)
                model = model.Where(grid.Where);

            //sorting
            if (string.IsNullOrEmpty(grid.SortColumn))
                grid.SortColumn = "id";

            model = model.OrderBy(grid.SortColumn, grid.SortOrder);

            //paging
            if (grid.PageIndex == 0)
                grid.PageIndex = 1;

            T[] data = grid.PageSize == 0 ?
                model.ToArray() :
                model.Skip((grid.PageIndex - 1) * grid.PageSize).Take(grid.PageSize).ToArray();

            //count
            var totalcount = model.Count();

            //converting in grid format
            var gridmodel = new GridModel
            {
                total = (int)Math.Ceiling((double)totalcount / grid.PageSize),
                page = grid.PageIndex,
                records = totalcount,
                rows = data,
                userdata = userdata
            };

            return gridmodel;
        }
    }
    
    public class GridModelBinder : IModelBinder
    {
        public bool BindModel(HttpActionContext actionContext, ModelBindingContext bindingContext)
        {
            if (bindingContext.ModelType != typeof(GridSettings))
                return false;

            //var contentFromInputStream = new StreamReader((actionContext.ControllerContext.Request.Properties["MS_HttpContext"] as System.Web.HttpContextWrapper).Request.InputStream).ReadToEnd();
            var request = actionContext.Request.Content.ReadAsStringAsync().Result;
            if (string.IsNullOrEmpty(request))
                return false;

            var qscoll = HttpUtility.ParseQueryString(request);
            try
            {
                string filters = qscoll["filters"];
                if (string.IsNullOrEmpty(filters))
                    filters = string.Format("{{\"groupOp\":\"AND\",\"rules\":[{{\"field\":\"{0}\",\"op\":\"{1}\",\"data\":\"{2}\"}}]}}",qscoll["searchField"], qscoll["searchOper"], qscoll["searchString"]);

                bindingContext.Model = new GridSettings()
                {
                    IsSearch = bool.Parse(qscoll["_search"] ?? "false"),
                    PageIndex = int.Parse(qscoll["page"] ?? "1"),
                    PageSize = int.Parse(qscoll["rows"] ?? "25"),
                    SortColumn = qscoll["sidx"] ?? "",
                    SortOrder = qscoll["sord"] ?? "asc",
                    Where = Filter.Create(filters)
                };
                return true;
            }
            catch(Exception ex)
            {
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, ex.ToString());
                return false;
            }
        }

        private T GetValue<T>(ModelBindingContext bindingContext, string key, T defaulValue)
        {
            var valueResult = bindingContext.ValueProvider.GetValue(key);
            if (valueResult == null)
                return defaulValue;
            bindingContext.ModelState.SetModelValue(key, valueResult);
            return (T)valueResult.ConvertTo(typeof(T));
        }
    }

    public class GridModelBinderProvider : ModelBinderProvider
    {
        public override IModelBinder GetBinder(HttpConfiguration configuration, Type modelType)
        {
            return new GridModelBinder();
        }
    }

    [ModelBinder(typeof(GridModelBinderProvider))]
    public class GridSettings
    {
        public bool IsSearch { get; set; }
        public int PageSize { get; set; }
        public int PageIndex { get; set; }
        public string SortColumn { get; set; }
        public string SortOrder { get; set; }

        public Filter Where { get; set; }
    }

    public class Filter
    {
        public string groupOp { get; set; }
        public Rule[] rules { get; set; }

        public static Filter Create(string jsonData)
        {
            try
            {
                if (string.IsNullOrEmpty(jsonData))
                    return null;
                
                var objData = Newtonsoft.Json.JsonConvert.DeserializeObject<Filter>(jsonData);
                return objData;
            }
            catch
            {
                return null;
            }
        }
    }

    public class Rule
    {
        public string field { get; set; }
        public string op { get; set; }
        public string data { get; set; }
    }

    public class GridModel
    {
        public int total;
        public int page;
        public int records;
        public dynamic[] rows;
        public dynamic userdata;
    }

    public static class DynamicLinqHelper
    {
        public static IQueryable<T> OrderBy<T>(this IQueryable<T> query, string sortColumn, string direction)
        {
            if (string.IsNullOrEmpty(sortColumn))
                return query;
            
            string methodName = string.Format("OrderBy{0}", string.IsNullOrEmpty(direction) || direction.ToLower() == "asc" ? "" : "Descending");
            var parameter = Expression.Parameter(query.ElementType, "p");

            MemberExpression memberAccess = null;
            foreach (var property in sortColumn.Split('.'))
                memberAccess = Expression.Property(memberAccess ?? ((Expression)parameter), property);

            // !!! TODO: memberAccess can be null
            var orderByLambda = Expression.Lambda(memberAccess, parameter);

            var result = Expression.Call(
                      typeof(Queryable),
                      methodName,
                      new[] { query.ElementType, memberAccess.Type },
                      query.Expression,
                      Expression.Quote(orderByLambda));

            return query.Provider.CreateQuery<T>(result);
        }

        public static IQueryable<T> Where<T>(this IQueryable<T> source, Filter gridfilter)
        {
            if (gridfilter == null)
                return source;
            
            Expression resultCondition = null;
            var parameter = Expression.Parameter(source.ElementType, "p");

            foreach (var rule in gridfilter.rules)
            {
                if (string.IsNullOrEmpty(rule.field)) continue;
            
                Expression memberAccess = null;
                foreach (var property in rule.field.Split('.'))
                    memberAccess = Expression.Property(memberAccess ?? parameter, property);

                //change param value type - necessary to getting bool from string
                Type t;
                object value;

                if (memberAccess.Type.Namespace.StartsWith("jqGridExtension"))
                {
                    memberAccess = Expression.Property(memberAccess, "Id");
                    t = memberAccess.Type;
                    if (rule.data == "-1") continue;
                }
                else
                {
                    t = Nullable.GetUnderlyingType(memberAccess.Type) ?? memberAccess.Type;
                }

                try
                {
                    value = (rule.data == null) ? null : Convert.ChangeType(rule.data, t);
                }
                catch (FormatException)
                {
                    value = rule.data;
                    memberAccess = Expression.Call(memberAccess, memberAccess.Type.GetMethod("ToString", Type.EmptyTypes));
                }

                var filter = Expression.Constant(value);
                var nullfilter = Expression.Constant(null);

                //switch operation
                Expression toLower;
                Expression condition = null;
                switch (rule.op)
                {
                    case "eq": //equal
                        if (value is string)
                        {
                            toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                            condition = Expression.Equal(toLower, Expression.Constant(value.ToString().ToLower()));
                        }
                        else
                            condition = Expression.Equal(memberAccess, filter);
                        
                        break;
                    case "ne"://not equal
                        condition = Expression.NotEqual(memberAccess, filter);
                        break;
                    case "lt": //less than
                        condition = Expression.LessThan(memberAccess, filter);
                        break;
                    case "le"://less than or equal
                        condition = Expression.LessThanOrEqual(memberAccess, filter);
                        break;
                    case "gt": //greater than
                        condition = Expression.GreaterThan(memberAccess, filter);
                        break;
                    case "ge"://greater than or equal
                        condition = Expression.GreaterThanOrEqual(memberAccess, filter);
                        break;
                    case "bw": //begins with
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Call(toLower, typeof(string).GetMethod("StartsWith", new[] { typeof(string) }), Expression.Constant(value.ToString().ToLower()));
                        break;
                    case "bn": //doesn"t begin with
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Not(Expression.Call(toLower, typeof(string).GetMethod("StartsWith", new[] { typeof(string) }), Expression.Constant(value.ToString().ToLower())));
                        break;
                    case "nu": //is null
                        condition = Expression.Equal(memberAccess, nullfilter);
                        break;
                    case "nn": //is not null
                        condition = Expression.NotEqual(memberAccess, nullfilter);
                        break;
                    case "ew": //ends with
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Call(toLower, typeof(string).GetMethod("EndsWith", new[] { typeof(string) }), Expression.Constant(value.ToString().ToLower()));
                        break;
                    case "en": //doesn"t end with
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Not(Expression.Call(toLower, typeof(string).GetMethod("EndsWith", new[] { typeof(string) }), Expression.Constant(value.ToString().ToLower())));
                        break;
                    case "in": //is in
                    case "cn": // contains
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Call(toLower, typeof(string).GetMethod("Contains"), Expression.Constant(value.ToString().ToLower()));
                        break;
                    case "ni": //is not in
                    case "nc":  //doesn't contain
                        toLower = Expression.Call(memberAccess, typeof(string).GetMethod("ToLower", Type.EmptyTypes));
                        condition = Expression.Not(Expression.Call(toLower, typeof(string).GetMethod("Contains"), Expression.Constant(value.ToString().ToLower())));
                        break;
                }

                if (gridfilter.groupOp == "AND")
                    resultCondition = resultCondition != null ? Expression.And(resultCondition, condition) : condition;
                else
                    resultCondition = resultCondition != null ? Expression.Or(resultCondition, condition) : condition;
            }

            if (resultCondition == null)
                return source;

            var lambda = Expression.Lambda(resultCondition, parameter);
            var result = Expression.Call(typeof(Queryable), "Where", new[] { source.ElementType }, source.Expression, lambda);

            return source.Provider.CreateQuery<T>(result);
        }
    }
    
    public class JQGridQueryableAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuted(HttpActionExecutedContext actionExecutedContext)
        {
            if (ResponseIsValid(actionExecutedContext.Response))
            {
                GridSettings grid;
                var qscoll = actionExecutedContext.Request.RequestUri.ParseQueryString();
                try
                {
                    string filters = qscoll["filters"];
                    if (string.IsNullOrEmpty(filters))
                        filters = string.Format("{{\"groupOp\":\"AND\",\"rules\":[{{\"field\":\"{0}\",\"op\":\"{1}\",\"data\":\"{2}\"}}]}}", qscoll["searchField"], qscoll["searchOper"], qscoll["searchString"]);

                    grid = new GridSettings()
                    {
                        IsSearch = bool.Parse(qscoll["_search"] ?? "false"),
                        PageIndex = int.Parse(qscoll["page"] ?? "1"),
                        PageSize = int.Parse(qscoll["rows"] ?? "25"),
                        SortColumn = qscoll["sidx"] ?? "",
                        SortOrder = qscoll["sord"] ?? "asc",
                        Where = Filter.Create(filters),
                    };
                }
                catch(Exception ex)
                {
                    throw new HttpResponseException(actionExecutedContext.Request.CreateErrorResponse(HttpStatusCode.BadRequest, ex.Message));
                }

                object responseObject;
                actionExecutedContext.Response.TryGetContentValue(out responseObject);
                if (responseObject is IQueryable)
                {
                    var robj = jqGridHelper.ApplyJqGridFilters(responseObject as IQueryable<object>, grid);
                    actionExecutedContext.Response = actionExecutedContext.Request.CreateResponse(HttpStatusCode.OK, robj);
                }
            }
        }

        private bool ResponseIsValid(HttpResponseMessage response)
        {
            return response != null && response.StatusCode == HttpStatusCode.OK && response.Content is ObjectContent;
        }
    }
}
