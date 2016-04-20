﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartStore.Core.Domain.Common;
using SmartStore.Core.Domain.Discounts;
using SmartStore.Core.Domain.Logging;
using SmartStore.Core.Domain.Orders;
using SmartStore.Core.Domain.Stores;
using SmartStore.Core.Domain.Tax;
using SmartStore.Core.Localization;
using SmartStore.Core.Logging;
using SmartStore.PayPal.Settings;
using SmartStore.Services;
using SmartStore.Services.Catalog;
using SmartStore.Services.Common;
using SmartStore.Services.Customers;
using SmartStore.Services.Directory;
using SmartStore.Services.Localization;
using SmartStore.Services.Media;
using SmartStore.Services.Orders;
using SmartStore.Services.Payments;
using SmartStore.Services.Tax;

namespace SmartStore.PayPal.Services
{
	public class PayPalService : IPayPalService
	{
		private readonly ICommonServices _services;
		private readonly IOrderService _orderService;
		private readonly IOrderTotalCalculationService _orderTotalCalculationService;
		private readonly IPaymentService _paymentService;
		private readonly IPriceCalculationService _priceCalculationService;
		private readonly ITaxService _taxService;
		private readonly Lazy<ICurrencyService> _currencyService;
		private readonly Lazy<IPictureService> _pictureService;
		private readonly Lazy<CompanyInformationSettings> _companyInfoSettings;

		public PayPalService(
			ICommonServices services,
			IOrderService orderService,
			IOrderTotalCalculationService orderTotalCalculationService,
			IPaymentService paymentService,
			IPriceCalculationService priceCalculationService,
			ITaxService taxService,
			Lazy<ICurrencyService> currencyService,
			Lazy<IPictureService> pictureService,
			Lazy<CompanyInformationSettings> companyInfoSettings)
		{
			_services = services;
			_orderService = orderService;
			_orderTotalCalculationService = orderTotalCalculationService;
			_paymentService = paymentService;
			_priceCalculationService = priceCalculationService;
			_taxService = taxService;
			_currencyService = currencyService;
			_pictureService = pictureService;
			_companyInfoSettings = companyInfoSettings;

			T = NullLocalizer.Instance;
			Logger = NullLogger.Instance;
		}

		public Localizer T { get; set; }
		public ILogger Logger { get; set; }

		private Dictionary<string, object> CreateAddress(Address addr)
		{
			var dic = new Dictionary<string, object>();

			dic.Add("line1", addr.Address1.Truncate(100));

			if (addr.Address2.HasValue())
			{
				dic.Add("line2", addr.Address2.Truncate(100));
			}

			dic.Add("city", addr.City.Truncate(50));

			if (addr.CountryId != 0 && addr.Country != null)
			{
				dic.Add("country_code", addr.Country.TwoLetterIsoCode);
			}

			dic.Add("postal_code", addr.ZipPostalCode.Truncate(20));

			if (addr.StateProvinceId != 0 && addr.StateProvince != null)
			{
				dic.Add("state", addr.StateProvince.Abbreviation.Truncate(100));
			}

			dic.Add("recipient_name", addr.GetFullName().Truncate(50));

			return dic;
		}

		public static string GetApiUrl(bool sandbox)
		{
			return sandbox ? "https://api.sandbox.paypal.com" : "https://api.paypal.com";
		}

		public static Dictionary<SecurityProtocolType, string> GetSecurityProtocols()
		{
			var dic = new Dictionary<SecurityProtocolType, string>();

			foreach (SecurityProtocolType protocol in Enum.GetValues(typeof(SecurityProtocolType)))
			{
				string friendlyName = null;
				switch (protocol)
				{
					case SecurityProtocolType.Ssl3:
						friendlyName = "SSL 3.0";
						break;
					case SecurityProtocolType.Tls:
						friendlyName = "TLS 1.0";
						break;
					case SecurityProtocolType.Tls11:
						friendlyName = "TLS 1.1";
						break;
					case SecurityProtocolType.Tls12:
						friendlyName = "TLS 1.2";
						break;
					default:
						friendlyName = protocol.ToString().ToUpper();
						break;
				}

				dic.Add(protocol, friendlyName);
			}
			return dic;
		}

		public void AddOrderNote(PayPalSettingsBase settings, Order order, string anyString)
		{
			try
			{
				if (order == null || anyString.IsEmpty() || (settings != null && !settings.AddOrderNotes))
					return;

				string[] orderNoteStrings = T("Plugins.SmartStore.PayPal.OrderNoteStrings").Text.SplitSafe(";");
				var faviconUrl = "{0}Plugins/{1}/Content/favicon.png".FormatInvariant(_services.WebHelper.GetStoreLocation(false), Plugin.SystemName);

				var sb = new StringBuilder();
				sb.AppendFormat("<img src=\"{0}\" style=\"float: left; width: 16px; height: 16px;\" />", faviconUrl);

				var note = orderNoteStrings.SafeGet(0).FormatInvariant(anyString);

				sb.AppendFormat("<span style=\"padding-left: 4px;\">{0}</span>", note);

				_orderService.AddOrderNote(order, sb.ToString());
			}
			catch { }
		}

		public void LogError(Exception exception, string shortMessage = null, string fullMessage = null, bool notify = false, IList<string> errors = null, bool isWarning = false)
		{
			try
			{
				if (exception != null)
				{
					shortMessage = exception.ToAllMessages();
					fullMessage = exception.ToString();
				}

				if (shortMessage.HasValue())
				{
					shortMessage = "PayPal. " + shortMessage;
					Logger.InsertLog(isWarning ? LogLevel.Warning : LogLevel.Error, shortMessage, fullMessage.EmptyNull());

					if (notify)
					{
						if (isWarning)
							_services.Notifier.Warning(new LocalizedString(shortMessage));
						else
							_services.Notifier.Error(new LocalizedString(shortMessage));
					}
				}
			}
			catch (Exception) { }

			if (errors != null && shortMessage.HasValue())
			{
				errors.Add(shortMessage);
			}
		}

		public PayPalPaymentInstruction ParsePaymentInstruction(dynamic json)
		{
			if (json == null)
				return null;

			DateTime dt;
			var result = new PayPalPaymentInstruction();

			try
			{
				result.ReferenceNumber = (string)json.reference_number;
				result.Type = (string)json.instruction_type;
				result.Note = (string)json.note;

				if (DateTime.TryParse((string)json.payment_due_date, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
				{
					result.DueDate = dt;
				}

				if (json.amount != null)
				{
					result.AmountCurrencyCode = (string)json.amount.currency;
					result.Amount = decimal.Parse((string)json.amount.value, CultureInfo.InvariantCulture);
				}

				var rbi = json.recipient_banking_instruction;

				if (rbi != null)
				{
					result.RecipientBanking = new PayPalPaymentInstruction.RecipientBankingInstruction();
					result.RecipientBanking.BankName = (string)rbi.bank_name;
					result.RecipientBanking.AccountHolderName = (string)rbi.account_holder_name;
					result.RecipientBanking.AccountNumber = (string)rbi.account_number;
					result.RecipientBanking.RoutingNumber = (string)rbi.routing_number;
					result.RecipientBanking.Iban = (string)rbi.international_bank_account_number;
					result.RecipientBanking.Bic = (string)rbi.bank_identifier_code;
				}

				if (json.links != null)
				{
					result.Link = (string)json.links[0].href;
				}
			}
			catch { }

			return result;
		}

		public string CreatePaymentInstruction(PayPalPaymentInstruction instruct)
		{
			if (instruct == null || instruct.RecipientBanking == null)
				return null;

			if (!instruct.IsManualBankTransfer && !instruct.IsPayUponInvoice)
				return null;

			var sb = new StringBuilder("<div>");
			var paragraphTemplate = "<div style='margin-bottom:10px;'>{0}</div>";
			var rowTemplate = "<span style='min-width:120px;'>{0}</span>: {1}<br />";
			var instructStrings = T("Plugins.SmartStore.PayPal.PaymentInstructionStrings").Text.SplitSafe(";");

			if (instruct.IsManualBankTransfer)
			{
				sb.AppendFormat(paragraphTemplate, T("Plugins.SmartStore.PayPal.ManualBankTransferNote"));

				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Reference), instruct.ReferenceNumber);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.AccountNumber), instruct.RecipientBanking.AccountNumber);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.AccountHolder), instruct.RecipientBanking.AccountHolderName);

				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Bank), instruct.RecipientBanking.BankName);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Iban), instruct.RecipientBanking.Iban);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Bic), instruct.RecipientBanking.Bic);
			}
			else if (instruct.IsPayUponInvoice)
			{
				string amount = null;
				var culture = new CultureInfo(_services.WorkContext.WorkingLanguage.LanguageCulture ?? "de-DE");

				try
				{
					var currency = _currencyService.Value.GetCurrencyByCode(instruct.AmountCurrencyCode);
					var format = (currency != null && currency.CustomFormatting.HasValue() ? currency.CustomFormatting : "C");

					amount = instruct.Amount.ToString(format, culture);
				}
				catch { }

				if (amount.IsEmpty())
				{
					amount = string.Concat(instruct.Amount.ToString("N"), " ", instruct.AmountCurrencyCode);
				}

				var intro = T("Plugins.SmartStore.PayPal.PayUponInvoiceLegalNote", _companyInfoSettings.Value.CompanyName.NaIfEmpty());

				if (instruct.Link.HasValue())
				{
					intro = "{0} <a href='{1}'>{2}</a>.".FormatInvariant(intro, instruct.Link, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Details));
				}

				sb.AppendFormat(paragraphTemplate, intro);

				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Bank), instruct.RecipientBanking.BankName);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.AccountHolder), instruct.RecipientBanking.AccountHolderName);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Iban), instruct.RecipientBanking.Iban);
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Bic), instruct.RecipientBanking.Bic);
				sb.Append("<br />");
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Amount), amount);
				if (instruct.DueDate.HasValue)
				{
					sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.PaymentDueDate), instruct.DueDate.Value.ToString("d", culture));
				}
				sb.AppendFormat(rowTemplate, instructStrings.SafeGet((int)PayPalPaymentInstructionItem.Reference), instruct.ReferenceNumber);
			}

			sb.Append("</div>");

			return sb.ToString();
		}

		public PayPalResponse CallApi(string method, string path, string accessToken, PayPalApiSettingsBase settings, string data, IList<string> errors = null)
		{
			var isJson = (data.HasValue() && data.StartsWith("{"));
			var encoding = (isJson ? Encoding.UTF8 : Encoding.ASCII);
			var result = new PayPalResponse();
			HttpWebResponse webResponse = null;

			var url = GetApiUrl(settings.UseSandbox) + path.EnsureStartsWith("/");

			if (method.IsCaseInsensitiveEqual("GET") && data.HasValue())
				url = url.EnsureEndsWith("?") + data;

			if (settings.SecurityProtocol.HasValue)
				ServicePointManager.SecurityProtocol = settings.SecurityProtocol.Value;

			var request = (HttpWebRequest)WebRequest.Create(url);
			request.Method = method;
			request.Accept = "application/json";
			request.ContentType = (isJson ? "application/json" : "application/x-www-form-urlencoded");

			try
			{
				if (HttpContext.Current != null && HttpContext.Current.Request != null)
					request.UserAgent = HttpContext.Current.Request.UserAgent;
				else
					request.UserAgent = Plugin.SystemName;
			}
			catch { }

			if (path.EmptyNull().EndsWith("/token"))
			{
				// see https://github.com/paypal/sdk-core-dotnet/blob/master/Source/SDK/OAuthTokenCredential.cs
				byte[] credentials = Encoding.UTF8.GetBytes("{0}:{1}".FormatInvariant(settings.ClientId, settings.Secret));

				request.Headers.Add("Authorization", "Basic " + Convert.ToBase64String(credentials));
			}
			else
			{
				request.Headers["Authorization"] = "Bearer " + accessToken.EmptyNull();
			}


			if (data.HasValue() && (method.IsCaseInsensitiveEqual("POST") || method.IsCaseInsensitiveEqual("PUT") || method.IsCaseInsensitiveEqual("PATCH")))
			{
				byte[] bytes = encoding.GetBytes(data);

				request.ContentLength = bytes.Length;

				using (var stream = request.GetRequestStream())
				{
					stream.Write(bytes, 0, bytes.Length);
				}
			}
			else
			{
				request.ContentLength = 0;
			}

			try
			{
				webResponse = request.GetResponse() as HttpWebResponse;
				result.Success = ((int)webResponse.StatusCode < 400);
			}
			catch (WebException wexc)
			{
				result.Success = false;
				result.ErrorMessage = wexc.ToString();
				webResponse = wexc.Response as HttpWebResponse;
			}
			catch (Exception exception)
			{
				result.Success = false;
				LogError(exception, errors: errors);
			}

			try
			{
				if (webResponse != null)
				{
					using (var reader = new StreamReader(webResponse.GetResponseStream(), Encoding.UTF8))
					{
						var rawResponse = reader.ReadToEnd();
						if (rawResponse.HasValue())
						{
							if (webResponse.ContentType.IsCaseInsensitiveEqual("application/json"))
							{
								if (rawResponse.StartsWith("["))
									result.Json = JArray.Parse(rawResponse);
								else
									result.Json = JObject.Parse(rawResponse);

								if (result.Json != null)
								{
									if (!result.Success)
									{
										var name = (string)result.Json.name;
										var message = (string)result.Json.message;

										if (name.IsEmpty())
											name = (string)result.Json.error;

										if (message.IsEmpty())
											message = (string)result.Json.error_description;

										result.ErrorMessage = "{0} ({1}).".FormatInvariant(message.NaIfEmpty(), name.NaIfEmpty());
									}
								}
							}
							else if (!result.Success)
							{							
								result.ErrorMessage = rawResponse;
							}
						}
					}

					if (!result.Success)
					{
						if (result.ErrorMessage.IsEmpty())
							result.ErrorMessage = webResponse.StatusDescription;

						LogError(null, result.ErrorMessage, result.Json == null ? null : result.Json.ToString(), false, errors);
					}
				}
			}
			catch (Exception exception)
			{
				LogError(exception, errors: errors);
			}
			finally
			{
				if (webResponse != null)
				{
					webResponse.Close();
					webResponse.Dispose();
				}
			}

			return result;
		}

		public PayPalResponse EnsureAccessToken(PayPalSessionData session, PayPalApiSettingsBase settings)
		{
			if (session.AccessToken.IsEmpty() || DateTime.UtcNow >= session.TokenExpiration)
			{
				var result = CallApi("POST", "/v1/oauth2/token", null, settings, "grant_type=client_credentials");

				if (result.Success)
				{
					session.AccessToken = (string)result.Json.access_token;

					var expireSeconds = ((string)result.Json.expires_in).ToInt(5 * 60);

					session.TokenExpiration = DateTime.UtcNow.AddSeconds(expireSeconds);
				}
				else
				{
					return result;
				}
			}

			return new PayPalResponse
			{
				Success = session.AccessToken.HasValue()
			};
		}

		public PayPalResponse UpsertCheckoutExperience(PayPalApiSettingsBase settings, PayPalSessionData session, Store store, string profileId)
		{
			PayPalResponse result;
			var name = store.Name;
			var logo = _pictureService.Value.GetPictureById(store.LogoPictureId);
			var path = "/v1/payment-experience/web-profiles";

			var data = new Dictionary<string, object>();
			var presentation = new Dictionary<string, object>();
			var inpuFields = new Dictionary<string, object>();

			if (profileId.IsEmpty())
			{
				result = CallApi("GET", path, session.AccessToken, settings, null);
				if (result.Success && result.Json != null)
				{
					foreach (var profile in result.Json)
					{
						var profileName = (string)profile.name;
						if (profileName.IsCaseInsensitiveEqual(name))
						{
							profileId = (string)profile.id;
							break;
						}
					}
				}
			}

			presentation.Add("brand_name", name);
			presentation.Add("locale_code", _services.WorkContext.WorkingLanguage.UniqueSeoCode.EmptyNull().ToUpper());

			if (logo != null)
				presentation.Add("logo_image", _pictureService.Value.GetPictureUrl(logo, showDefaultPicture: false, storeLocation: store.Url));

			inpuFields.Add("allow_note", false);
			inpuFields.Add("no_shipping", 0);
			inpuFields.Add("address_override", 1);

			data.Add("name", name);
			data.Add("presentation", presentation);
			data.Add("input_fields", inpuFields);

			if (profileId.HasValue())
				path = string.Concat(path, "/", HttpUtility.UrlPathEncode(profileId));

			result = CallApi(profileId.HasValue() ? "PUT" : "POST", path, session.AccessToken, settings, JsonConvert.SerializeObject(data));

			if (result.Success)
			{
				if (result.Json != null)
					result.Id = (string)result.Json.id;
				else
					result.Id = profileId;
			}

			return result;
		}

		public PayPalResponse GetPayment(PayPalApiSettingsBase settings, PayPalSessionData session)
		{
			var result = CallApi("GET", "/v1/payments/payment/" + session.PaymentId, session.AccessToken, settings, null);

			if (result.Success && result.Json != null)
			{
				result.Id = (string)result.Json.id;
			}

			return result;
		}

		public PayPalResponse CreatePayment(
			PayPalApiSettingsBase settings,
			PayPalSessionData session,
			List<OrganizedShoppingCartItem> cart,
			string providerSystemName,
			string returnUrl,
			string cancelUrl)
		{
			var store = _services.StoreContext.CurrentStore;
			var customer = _services.WorkContext.CurrentCustomer;
			var language = _services.WorkContext.WorkingLanguage;
			var currencyCode = store.PrimaryStoreCurrency.CurrencyCode;

			Discount orderAppliedDiscount;
			List<AppliedGiftCard> appliedGiftCards;
			int redeemedRewardPoints = 0;
			decimal redeemedRewardPointsAmount;
			decimal orderDiscountInclTax;
			decimal totalOrderItems = decimal.Zero;

			var includingTax = (_services.WorkContext.GetTaxDisplayTypeFor(customer, store.Id) == TaxDisplayType.IncludingTax);

			var shipping = (_orderTotalCalculationService.GetShoppingCartShippingTotal(cart) ?? decimal.Zero);

			var paymentFee = _paymentService.GetAdditionalHandlingFee(cart, providerSystemName);

			var total = (_orderTotalCalculationService.GetShoppingCartTotal(cart, out orderDiscountInclTax, out orderAppliedDiscount, out appliedGiftCards,
				out redeemedRewardPoints, out redeemedRewardPointsAmount) ?? decimal.Zero);

			var path = "/v1/payments/payment";
			var data = new Dictionary<string, object>();
			var redirectUrls = new Dictionary<string, object>();
			var payer = new Dictionary<string, object>();
			var payerInfo = new Dictionary<string, object>();
			var transaction = new Dictionary<string, object>();
			var amount = new Dictionary<string, object>();
			var amountDetails = new Dictionary<string, object>();
			var items = new List<Dictionary<string, object>>();
			var itemList = new Dictionary<string, object>();

			if (session.PaymentId.HasValue())
				path = string.Concat(path, "/", HttpUtility.UrlPathEncode(session.PaymentId));

			// "PayPal PLUS only supports transaction type “Sale” (instant settlement)"
			if (providerSystemName == PayPalPlusProvider.SystemName)
				data.Add("intent", "sale");
			else
				data.Add("intent", settings.TransactMode == TransactMode.AuthorizeAndCapture ? "sale" : "authorize");

			if (settings.ExperienceProfileId.HasValue())
				data.Add("experience_profile_id", settings.ExperienceProfileId);

			// redirect urls
			if (returnUrl.HasValue())
				redirectUrls.Add("return_url", returnUrl);

			if (cancelUrl.HasValue())
				redirectUrls.Add("cancel_url", cancelUrl);

			if (redirectUrls.Any())
				data.Add("redirect_urls", redirectUrls);

			// payer
			payer.Add("payment_method", "paypal");

			if (customer.BillingAddress != null)
			{
				payerInfo.Add("billing_address", CreateAddress(customer.BillingAddress));

				payer.Add("payer_info", payerInfo);
			}

			data.Add("payer", payer);

			// line items
			foreach (var item in cart)
			{
				decimal unitPriceTaxRate = decimal.Zero;
				decimal unitPrice = _priceCalculationService.GetUnitPrice(item, true);
				decimal productPrice = _taxService.GetProductPrice(item.Item.Product, unitPrice, includingTax, customer, out unitPriceTaxRate);

				var line = new Dictionary<string, object>();
				line.Add("quantity", item.Item.Quantity);
				line.Add("name", item.Item.Product.GetLocalized(x => x.Name, language.Id, true, false).Truncate(127));
				line.Add("price", Math.Round(productPrice, 2));
				line.Add("currency", currencyCode);
				line.Add("sku", item.Item.Product.Sku.Truncate(50));
				items.Add(line);

				totalOrderItems += (productPrice * item.Item.Quantity);
			}

			var itemsPlusMisc = (totalOrderItems + shipping + paymentFee);

			if (total != itemsPlusMisc)
			{
				var line = new Dictionary<string, object>();
				line.Add("quantity", "1");
				line.Add("name", T("Plugins.SmartStore.PayPal.Other").Text.Truncate(127));
				line.Add("price", Math.Round(total - itemsPlusMisc, 2));
				line.Add("currency", currencyCode);
				items.Add(line);

				totalOrderItems += (total - itemsPlusMisc);
			}

			itemList.Add("items", items);
			if (customer.ShippingAddress != null)
			{
				itemList.Add("shipping_address", CreateAddress(customer.ShippingAddress));
			}

			// transactions
			amountDetails.Add("shipping", Math.Round(shipping, 2));
			amountDetails.Add("subtotal", Math.Round(totalOrderItems, 2));
			if (!includingTax)
			{
				// "To avoid rounding errors we recommend not submitting tax amounts on line item basis. 
				// Calculated tax amounts for the entire shopping basket may be submitted in the amount objects.
				// In this case the item amounts will be treated as amounts excluding tax.
				// In a B2C scenario, where taxes are included, no taxes should be submitted to PayPal."

				SortedDictionary<decimal, decimal> taxRates = null;
				var taxTotal = _orderTotalCalculationService.GetTaxTotal(cart, out taxRates);

				amountDetails.Add("tax", Math.Round(taxTotal, 2));
			}
			if (paymentFee != decimal.Zero)
			{
				amountDetails.Add("handling_fee", Math.Round(paymentFee, 2));
			}

			amount.Add("total", Math.Round(total, 2));
			amount.Add("currency", currencyCode);
			amount.Add("details", amountDetails);

			transaction.Add("amount", amount);
			transaction.Add("item_list", itemList);
			transaction.Add("invoice_number", session.OrderGuid.ToString());

			data.Add("transactions", new List<Dictionary<string, object>> { transaction });

			var result = CallApi("POST", path, session.AccessToken, settings, JsonConvert.SerializeObject(data));

			if (result.Success && result.Json != null)
			{
				result.Id = (string)result.Json.id;

				//Logger.InsertLog(LogLevel.Information, "PayPal PLUS", JsonConvert.SerializeObject(data, Formatting.Indented) + "\r\n\r\n" + result.Json.ToString());
			}

			return result;
		}

		public PayPalResponse ExecutePayment(PayPalApiSettingsBase settings, PayPalSessionData session)
		{
			var data = new Dictionary<string, object>();
			data.Add("payer_id", session.PayerId);

			var result = CallApi("POST", "/v1/payments/payment/{0}/execute".FormatInvariant(session.PaymentId), session.AccessToken, settings, JsonConvert.SerializeObject(data));

			if (result.Success && result.Json != null)
			{
				result.Id = (string)result.Json.id;

				//Logger.InsertLog(LogLevel.Information, "PayPal PLUS", JsonConvert.SerializeObject(data, Formatting.Indented) + "\r\n\r\n" + result.Json.ToString());
			}

			return result;
		}
	}


	public class PayPalResponse
	{
		public bool Success { get; set; }
		public dynamic Json { get; set; }
		public string ErrorMessage { get; set; }
		public string Id { get; set; }
	}

	public class PayPalSessionData
	{
		public PayPalSessionData()
		{
			OrderGuid = Guid.NewGuid();
		}

		public string AccessToken { get; set; }
		public DateTime TokenExpiration { get; set; }
		public string PaymentId { get; set; }
		public string PayerId { get; set; }
		public string ApprovalUrl { get; set; }
		public Guid OrderGuid { get; private set; }
		public PayPalPaymentInstruction PaymentInstruction { get; set; }
	}

	public class PayPalPaymentInstruction
	{
		public string ReferenceNumber { get; set; }
		public string Type { get; set; }
		public decimal Amount { get; set; }
		public string AmountCurrencyCode { get; set; }
		public DateTime? DueDate { get; set; }
		public string Note { get; set; }
		public string Link { get; set; }

		public RecipientBankingInstruction RecipientBanking { get; set; }

		public bool IsManualBankTransfer
		{
			get { return Type.IsCaseInsensitiveEqual("MANUAL_BANK_TRANSFER"); }
		}

		public bool IsPayUponInvoice
		{
			get { return Type.IsCaseInsensitiveEqual("PAY_UPON_INVOICE"); }
		}

		public class RecipientBankingInstruction
		{
			public string BankName { get; set; }
			public string AccountHolderName { get; set; }
			public string AccountNumber { get; set; }
			public string RoutingNumber { get; set; }
			public string Iban { get; set; }
			public string Bic { get; set; }
		}
	}
}