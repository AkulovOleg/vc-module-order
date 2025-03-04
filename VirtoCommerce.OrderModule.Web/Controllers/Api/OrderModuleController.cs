using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Cors;
using System.Web.Http.Description;
using CacheManager.Core;
using TheArtOfDev.HtmlRenderer.PdfSharp;
using VirtoCommerce.Domain.Cart.Services;
using VirtoCommerce.Domain.Common;
using VirtoCommerce.Domain.Order.Model;
using VirtoCommerce.Domain.Order.Services;
using VirtoCommerce.Domain.Payment.Model;
using VirtoCommerce.Domain.Store.Services;
using VirtoCommerce.OrderModule.Data.Notifications;
using VirtoCommerce.OrderModule.Data.Repositories;
using VirtoCommerce.OrderModule.Data.Services;
using VirtoCommerce.OrderModule.Web.BackgroundJobs;
using VirtoCommerce.OrderModule.Web.Model;
using VirtoCommerce.OrderModule.Web.Security;
using VirtoCommerce.Platform.Core.ChangeLog;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.Notifications;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.Platform.Core.Web.Security;
using VirtoCommerce.Platform.Data.Common;
using webModel = VirtoCommerce.OrderModule.Web.Model;

namespace VirtoCommerce.OrderModule.Web.Controllers.Api
{
    [RoutePrefix("api/order/customerOrders")]
    public class OrderModuleController : ApiController
    {
        private readonly ICustomerOrderService _customerOrderService;
        private readonly ICustomerOrderSearchService _searchService;
        private readonly IUniqueNumberGenerator _uniqueNumberGenerator;
        private readonly IStoreService _storeService;
        private readonly ICacheManager<object> _cacheManager;
        private readonly Func<IOrderRepository> _repositoryFactory;
        private readonly ISecurityService _securityService;
        private readonly IPermissionScopeService _permissionScopeService;
        private readonly ICustomerOrderBuilder _customerOrderBuilder;
        private readonly IShoppingCartService _cartService;
        private readonly INotificationManager _notificationManager;
        private readonly INotificationTemplateResolver _notificationTemplateResolver;
        private readonly IChangeLogService _changeLogService;
        private readonly ICustomerOrderTotalsCalculator _totalsCalculator;
        private static readonly object _lockObject = new object();

        public OrderModuleController(
            ICustomerOrderService customerOrderService,
            ICustomerOrderSearchService searchService,
            IStoreService storeService,
            IUniqueNumberGenerator numberGenerator,
            ICacheManager<object> cacheManager,
            Func<IOrderRepository> repositoryFactory,
            IPermissionScopeService permissionScopeService,
            ISecurityService securityService,
            ICustomerOrderBuilder customerOrderBuilder,
            IShoppingCartService cartService,
            INotificationManager notificationManager,
            INotificationTemplateResolver notificationTemplateResolver,
            IChangeLogService changeLogService,
            ICustomerOrderTotalsCalculator totalsCalculator)
        {
            _customerOrderService = customerOrderService;
            _searchService = searchService;
            _uniqueNumberGenerator = numberGenerator;
            _storeService = storeService;
            _cacheManager = cacheManager;
            _repositoryFactory = repositoryFactory;
            _securityService = securityService;
            _permissionScopeService = permissionScopeService;
            _customerOrderBuilder = customerOrderBuilder;
            _cartService = cartService;
            _notificationManager = notificationManager;
            _notificationTemplateResolver = notificationTemplateResolver;
            _changeLogService = changeLogService;
            _totalsCalculator = totalsCalculator;
        }

        /// <summary>
        /// Search customer orders by given criteria
        /// </summary>
        /// <param name="criteria">criteria</param>
        [HttpPost]
        [Route("search")]
        [ResponseType(typeof(webModel.CustomerOrderSearchResult))]
        public async Task<IHttpActionResult> Search(CustomerOrderSearchCriteria criteria)
        {
            //Scope bound ACL filtration
            criteria = await FilterOrderSearchCriteria(HttpContext.Current.User.Identity.Name, criteria);

            var result = _searchService.SearchCustomerOrders(criteria);
            var retVal = new CustomerOrderSearchResult
            {
                CustomerOrders = result.Results.ToList(),
                TotalCount = result.TotalCount
            };
            return Ok(retVal);
        }

        /// <summary>
        /// Find customer order by number
        /// </summary>
        /// <remarks>Return a single customer order with all nested documents or null if order was not found</remarks>
        /// <param name="number">customer order number</param>
        /// <param name="respGroup"></param>
        [HttpGet]
        [Route("number/{number}")]
        [ResponseType(typeof(CustomerOrder))]
        public async Task<IHttpActionResult> GetByNumber(string number, [FromUri] string respGroup = null)
        {
            var searchCriteria = AbstractTypeFactory<CustomerOrderSearchCriteria>.TryCreateInstance();
            searchCriteria.Number = number;

            var userName = User.Identity.Name;
            var user = await _securityService.FindByNameAsync(userName, UserDetails.Reduced);

            searchCriteria.ResponseGroup = OrderReadPricesPermission.ApplyResponseGroupFiltering(user, _securityService.GetUserPermissions(userName), respGroup);

            var result = _searchService.SearchCustomerOrders(searchCriteria);

            var retVal = result.Results.FirstOrDefault();
            if (retVal != null)
            {
                var scopes = _permissionScopeService.GetObjectPermissionScopeStrings(retVal).ToArray();
                if (!_securityService.UserHasAnyPermission(User.Identity.Name, scopes, OrderPredefinedPermissions.Read))
                {
                    throw new HttpResponseException(HttpStatusCode.Unauthorized);
                }

                //Set scopes for UI scope bounded ACL checking
                retVal.Scopes = scopes;
            }

            return Ok(retVal);
        }

        /// <summary>
        /// Find customer order by id
        /// </summary>
        /// <remarks>Return a single customer order with all nested documents or null if order was not found</remarks>
        /// <param name="id">customer order id</param>
        /// <param name="respGroup"></param>
        [HttpGet]
        [Route("{id}")]
        [ResponseType(typeof(CustomerOrder))]
        public async Task<IHttpActionResult> GetById(string id, [FromUri] string respGroup = null)
        {
            var userName = User.Identity.Name;
            var user = await _securityService.FindByNameAsync(userName, UserDetails.Reduced);

            respGroup = OrderReadPricesPermission.ApplyResponseGroupFiltering(user, _securityService.GetUserPermissions(userName), respGroup);

            var result = _customerOrderService.GetByIds(new[] { id }, respGroup).FirstOrDefault();

            if (result == null)
            {
                return NotFound();
            }

            //Scope bound security check
            var scopes = _permissionScopeService.GetObjectPermissionScopeStrings(result).ToArray();

            if (!_securityService.UserHasAnyPermission(User.Identity.Name, scopes, OrderPredefinedPermissions.Read))
            {
                throw new HttpResponseException(HttpStatusCode.Unauthorized);
            }

            //Set scopes for UI scope bounded ACL checking
            result.Scopes = scopes;

            return Ok(result);
        }

        /// <summary>
        /// Calculate order totals after changes
        /// </summary>
        /// <remarks>Return order with recalculated totals</remarks>
        /// <param name="order">Customer order</param>
        [HttpPut]
        [Route("recalculate")]
        [ResponseType(typeof(CustomerOrder))]
        public IHttpActionResult CalculateTotals(CustomerOrder order)
        {
            _totalsCalculator.CalculateTotals(order);

            return Ok(order);
        }

        /// <summary>
        /// Register customer order payment in external payment system
        /// </summary>
        /// <remarks>Used in storefront checkout or manual order payment registration</remarks>
        /// <param name="orderId">customer order id</param>
        /// <param name="paymentId">payment id</param>
        /// <param name="bankCardInfo">banking card information</param>
        [HttpPost]
        [Route("{orderId}/processPayment/{paymentId}")]
        [ResponseType(typeof(ProcessPaymentResult))]
        public IHttpActionResult ProcessOrderPayments(string orderId, string paymentId,
            [SwaggerOptional] BankCardInfo bankCardInfo)
        {
            var order = _customerOrderService.GetByIds(new[] { orderId }, CustomerOrderResponseGroup.Full.ToString())
                .FirstOrDefault();

            if (order == null)
            {
                var searchCriteria = AbstractTypeFactory<CustomerOrderSearchCriteria>.TryCreateInstance();
                searchCriteria.Number = orderId;
                searchCriteria.ResponseGroup = CustomerOrderResponseGroup.Full.ToString();

                order = _searchService.SearchCustomerOrders(searchCriteria).Results.FirstOrDefault();
            }

            if (order == null)
            {
                throw new InvalidOperationException($"Cannot find order with ID {orderId}");
            }

            var payment = order.InPayments.FirstOrDefault(x => x.Id == paymentId);
            if (payment == null)
            {
                throw new InvalidOperationException($"Cannot find payment with ID {paymentId}");
            }

            var store = _storeService.GetById(order.StoreId);
            var paymentMethod = store.PaymentMethods.FirstOrDefault(x => x.Code == payment.GatewayCode);
            if (paymentMethod == null)
            {
                throw new InvalidOperationException($"Cannot find payment method with code {payment.GatewayCode}");
            }

            var context = new ProcessPaymentEvaluationContext
            {
                Order = order,
                Payment = payment,
                Store = store,
                BankCardInfo = bankCardInfo
            };

            var result = paymentMethod.ProcessPayment(context);
            if (result.OuterId != null)
            {
                payment.OuterId = result.OuterId;
            }

            _customerOrderService.SaveChanges(new[] { order });

            return Ok(result);
        }

        /// <summary>
        /// Create new customer order based on shopping cart.
        /// </summary>
        /// <param name="id">shopping cart id</param>
        [HttpPost]
        [ResponseType(typeof(CustomerOrder))]
        [Route("{id}")]
        [CheckPermission(Permission = OrderPredefinedPermissions.Create)]
        [EnableCors(origins: "*", headers: "*", methods: "*")]
        public async Task<IHttpActionResult> CreateOrderFromCart(string id)
        {
            CustomerOrder retVal;

            using (await AsyncLock.GetLockByKey(id).LockAsync())
            {
                var cart = _cartService.GetByIds(new[] { id }).FirstOrDefault();
                retVal = _customerOrderBuilder.PlaceCustomerOrderFromCart(cart);
            }

            return Ok(retVal);
        }

        /// <summary>
        /// Add new customer order to system
        /// </summary>
        /// <param name="customerOrder">customer order</param>
        [HttpPost]
        [Route("")]
        [ResponseType(typeof(CustomerOrder))]
        [CheckPermission(Permission = OrderPredefinedPermissions.Create)]
        public IHttpActionResult CreateOrder(CustomerOrder customerOrder)
        {
            _customerOrderService.SaveChanges(new[] { customerOrder });
            return Ok(customerOrder);
        }

        /// <summary>
        ///  Update a existing customer order 
        /// </summary>
        /// <param name="customerOrder">customer order</param>
        [HttpPut]
        [Route("")]
        [ResponseType(typeof(void))]
        public IHttpActionResult Update(CustomerOrder customerOrder)
        {
            //Check scope bound permission
            var scopes = _permissionScopeService.GetObjectPermissionScopeStrings(customerOrder).ToArray();
            if (!_securityService.UserHasAnyPermission(User.Identity.Name, scopes, OrderPredefinedPermissions.Read))
            {
                throw new HttpResponseException(HttpStatusCode.Unauthorized);
            }

            _customerOrderService.SaveChanges(new[] { customerOrder });
            return StatusCode(HttpStatusCode.NoContent);
        }

        /// <summary>
        /// Get new shipment for specified customer order
        /// </summary>
        /// <remarks>Return new shipment document with populates all required properties.</remarks>
        /// <param name="id">customer order id </param>
        [HttpGet]
        [Route("{id}/shipments/new")]
        [ResponseType(typeof(Shipment))]
        public IHttpActionResult GetNewShipment(string id)
        {
            var order = _customerOrderService.GetByIds(new[] { id }, CustomerOrderResponseGroup.Full.ToString())
                .FirstOrDefault();
            if (order != null)
            {
                var retVal = AbstractTypeFactory<Shipment>.TryCreateInstance();

                retVal.Id = Guid.NewGuid().ToString();
                retVal.Currency = order.Currency;
                retVal.Status = "New";

                var store = _storeService.GetById(order.StoreId);
                var numberTemplate = store.Settings.GetSettingValue("Order.ShipmentNewNumberTemplate", "SH{0:yyMMdd}-{1:D5}");
                retVal.Number = _uniqueNumberGenerator.GenerateNumber(numberTemplate);

                return Ok(retVal);

                ////Detect not whole shipped items
                ////TechDebt: LineItem partial shipping
                ////var shippedLineItemIds = order.Shipments.SelectMany(x => x.Items).Select(x => x.LineItemId);

                ////TechDebt: Add check for digital products (don't add to shipment)
                //retVal.Items = order.Items.Where(x => !shippedLineItemIds.Contains(x.Id))
                ////              .Select(x => new coreModel.ShipmentItem(x)).ToList();
                ////return Ok(retVal.ToWebModel());
            }

            return NotFound();
        }

        /// <summary>
        /// Get new payment for specified customer order
        /// </summary>
        /// <remarks>Return new payment  document with populates all required properties.</remarks>
        /// <param name="id">customer order id </param>
        [HttpGet]
        [Route("{id}/payments/new")]
        [ResponseType(typeof(PaymentIn))]
        public IHttpActionResult GetNewPayment(string id)
        {
            var order = _customerOrderService.GetByIds(new[] { id }, CustomerOrderResponseGroup.Full.ToString())
                .FirstOrDefault();
            if (order != null)
            {
                var retVal = AbstractTypeFactory<PaymentIn>.TryCreateInstance();
                retVal.Id = Guid.NewGuid().ToString();
                retVal.Currency = order.Currency;
                retVal.CustomerId = order.CustomerId;
                retVal.Status = retVal.PaymentStatus.ToString();

                var store = _storeService.GetById(order.StoreId);
                var numberTemplate = store.Settings.GetSettingValue("Order.PaymentInNewNumberTemplate", "PI{0:yyMMdd}-{1:D5}");
                retVal.Number = _uniqueNumberGenerator.GenerateNumber(numberTemplate);
                return Ok(retVal);
            }

            return NotFound();
        }

        /// <summary>
        ///  Delete a whole customer orders
        /// </summary>
        /// <param name="ids">customer order ids for delete</param>
        [HttpDelete]
        [Route("")]
        [ResponseType(typeof(void))]
        [CheckPermission(Permission = OrderPredefinedPermissions.Delete)]
        public IHttpActionResult DeleteOrdersByIds([FromUri] string[] ids)
        {
            _customerOrderService.Delete(ids);
            return StatusCode(HttpStatusCode.NoContent);
        }

        /// <summary>
        ///  Get a some order statistic information for Commerce manager dashboard
        /// </summary>
        /// <param name="start">start interval date</param>
        /// <param name="end">end interval date</param>
        [HttpGet]
        [Route("~/api/order/dashboardStatistics")]
        [ResponseType(typeof(webModel.DashboardStatisticsResult))]
        public IHttpActionResult GetDashboardStatistics([FromUri] DateTime? start = null, [FromUri] DateTime? end = null)
        {
            webModel.DashboardStatisticsResult retVal;
            start = start ?? DateTime.UtcNow.AddYears(-1);
            end = end ?? DateTime.UtcNow;

            // Hack: to compinsate for incorrect Local dates to UTC
            end = end.Value.AddDays(2);
            var cacheKey = string.Join(":", "Statistic", start.Value.ToString("yyyy-MM-dd"), end.Value.ToString("yyyy-MM-dd"));
            lock (_lockObject)
            {
                retVal = _cacheManager.Get(cacheKey, "OrderModuleRegion", () =>
                {
                    var collectStaticJob = new CollectOrderStatisticJob(_repositoryFactory);
                    return collectStaticJob.CollectStatistics(start.Value, end.Value);
                });
            }

            return Ok(retVal);
        }

        /// <summary>
        /// Payment callback operation used by external payment services to inform post process payment in our system
        /// </summary>
        /// <param name="callback">payment callback parameters</param>
        [HttpPost]
        [Route("~/api/paymentcallback")]
        [ResponseType(typeof(PostProcessPaymentResult))]
        public IHttpActionResult PostProcessPayment(webModel.PaymentCallbackParameters callback)
        {
            var parameters = new NameValueCollection();
            foreach (var param in callback?.Parameters ?? Array.Empty<KeyValuePair>())
            {
                parameters.Add(param.Key, param.Value);
            }

            var orderId = parameters.Get("orderid");
            if (string.IsNullOrEmpty(orderId))
            {
                throw new InvalidOperationException("the 'orderid' parameter must be passed");
            }

            //some payment method require customer number to be passed and returned. First search customer order by number
            var searchCriteria = AbstractTypeFactory<CustomerOrderSearchCriteria>.TryCreateInstance();
            searchCriteria.Number = orderId;
            searchCriteria.ResponseGroup = CustomerOrderResponseGroup.Full.ToString();
            //if order not found by order number search by order id
            var order = _searchService.SearchCustomerOrders(searchCriteria).Results.FirstOrDefault() ??
                        _customerOrderService.GetByIds(new[] { orderId }, CustomerOrderResponseGroup.Full.ToString())
                            .FirstOrDefault();

            if (order == null)
            {
                throw new InvalidOperationException($"Cannot find order with ID {orderId}");
            }

            var orderPaymentsCodes = order.InPayments.Select(x => x.GatewayCode).Distinct().ToArray();
            var store = _storeService.GetById(order.StoreId);
            var paymentMethodCode = parameters.Get("code");
            //Need to use concrete  payment method if it code passed otherwise use all order payment methods
            var paymentMethods = store.PaymentMethods.Where(x => x.IsActive)
                .Where(x => orderPaymentsCodes.Contains(x.Code));
            if (!string.IsNullOrEmpty(paymentMethodCode))
            {
                paymentMethods = paymentMethods.Where(x => x.Code.EqualsInvariant(paymentMethodCode));
            }

            foreach (var paymentMethod in paymentMethods)
            {
                //Each payment method must check that these parameters are addressed to it
                var result = paymentMethod.ValidatePostProcessRequest(parameters);
                if (result.IsSuccess)
                {
                    var paymentOuterId = result.OuterId;
                    var payment = order.InPayments.FirstOrDefault(x => string.IsNullOrEmpty(x.OuterId) || x.OuterId == paymentOuterId);
                    if (payment == null)
                    {
                        throw new InvalidOperationException(@"Cannot find payment");
                    }

                    var context = new PostProcessPaymentEvaluationContext
                    {
                        Order = order,
                        Payment = payment,
                        Store = store,
                        OuterId = paymentOuterId,
                        Parameters = parameters
                    };
                    var retVal = paymentMethod.PostProcessPayment(context);
                    if (retVal != null)
                    {
                        _customerOrderService.SaveChanges(new[] { order });

                        // order Number is required
                        retVal.OrderId = order.Number;
                    }

                    return Ok(retVal);
                }
            }

            return Ok(new PostProcessPaymentResult { ErrorMessage = "Payment method not found" });
        }

        [HttpGet]
        [Route("invoice/{orderNumber}")]
        [SwaggerFileResponse]
        public async Task<IHttpActionResult> GetInvoicePdf(string orderNumber)
        {
            var searchCriteria = AbstractTypeFactory<CustomerOrderSearchCriteria>.TryCreateInstance();
            var userName = User.Identity.Name;
            var user = await _securityService.FindByNameAsync(userName, UserDetails.Reduced);
            var responseGroup = OrderReadPricesPermission.ApplyResponseGroupFiltering(user, _securityService.GetUserPermissions(userName), null);

            searchCriteria.Number = orderNumber;
            searchCriteria.Take = 1;
            searchCriteria.ResponseGroup = responseGroup;

            var order = _searchService.SearchCustomerOrders(searchCriteria).Results.FirstOrDefault();

            if (order == null)
            {
                throw new InvalidOperationException($"Cannot find order with number {orderNumber}");
            }

            var invoice = _notificationManager.GetNewNotification<InvoiceEmailNotification>(order.StoreId, "Store", order.LanguageCode);

            invoice.CustomerOrder = order;
            _notificationTemplateResolver.ResolveTemplate(invoice);

            var stream = new MemoryStream();
            var pdf = PdfGenerator.GeneratePdf(invoice.Body, PdfSharp.PageSize.A4);
            pdf.Save(stream, false);
            stream.Seek(0, SeekOrigin.Begin);

            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            result.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            return ResponseMessage(result);
        }

        [HttpGet]
        [Route("{id}/changes")]
        [ResponseType(typeof(OperationLog[]))]
        public IHttpActionResult GetOrderChanges(string id)
        {
            var result = new OperationLog[] { };
            var order = _customerOrderService.GetByIds(new[] { id }).FirstOrDefault();
            if (order != null)
            {
                _changeLogService.LoadChangeLogs(order);
                //Load general change log for order
                result = order.GetFlatObjectsListWithInterface<IHasChangesHistory>()
                    .Distinct()
                    .SelectMany(x => x.OperationsLog)
                    .OrderBy(x => x.CreatedDate)
                    .Distinct().ToArray();
            }

            return Ok(result);
        }

        private async Task<CustomerOrderSearchCriteria> FilterOrderSearchCriteria(string userName, CustomerOrderSearchCriteria criteria)
        {
            var user = await _securityService.FindByNameAsync(userName, UserDetails.Reduced);

            criteria.ResponseGroup = OrderReadPricesPermission.ApplyResponseGroupFiltering(user, _securityService.GetUserPermissions(userName), criteria.ResponseGroup);

            if (!_securityService.UserHasAnyPermission(userName, null, OrderPredefinedPermissions.Read))
            {
                //Get defined user 'read' permission scopes
                var readPermissionScopes = _securityService.GetUserPermissions(userName)
                    .Where(x => x.Id.StartsWith(OrderPredefinedPermissions.Read))
                    .SelectMany(x => x.AssignedScopes)
                    .ToList();

                //Check user has a scopes
                //Stores
                criteria.StoreIds = readPermissionScopes.OfType<OrderStoreScope>()
                    .Select(x => x.Scope)
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToArray();

                //employee id
                var responsibleScope = readPermissionScopes.OfType<OrderResponsibleScope>().FirstOrDefault();
                if (responsibleScope != null)
                {
                    criteria.EmployeeId = userName;
                }
            }

            return criteria;
        }
    }
}
