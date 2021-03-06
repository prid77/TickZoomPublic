﻿#region Copyright
/*
 * Software: TickZoom Trading Platform
 * Copyright 2009 M. Wayne Walter
 * 
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 2.1 of the License, or (at your option) any later version.
 * 
 * Business use restricted to 30 days except as otherwise stated in
 * in your Service Level Agreement (SLA).
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, see <http://www.tickzoom.org/wiki/Licenses>
 * or write to Free Software Foundation, Inc., 51 Franklin Street,
 * Fifth Floor, Boston, MA  02110-1301, USA.
 * 
 */
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using TickZoom.Api;
using TickZoom.FIX;

namespace TickZoom.MBTFIX
{

	[SkipDynamicLoad]
	public class MBTFIXProvider : FIXProviderSupport, PhysicalOrderHandler
	{
		private static readonly Log log = Factory.SysLog.GetLogger(typeof(MBTFIXProvider));
		private static readonly bool info = log.IsDebugEnabled;
		private static readonly bool debug = log.IsDebugEnabled;
		private static readonly bool trace = log.IsTraceEnabled;
		private static long nextConnectTime = 0L;
		private readonly object orderAlgorithmLocker = new object();
        private Dictionary<long,OrderAlgorithm> orderAlgorithms = new Dictionary<long,OrderAlgorithm>();
        private object openOrdersLocker = new object();
		private Dictionary<string,PhysicalOrder> openOrders = new Dictionary<string,PhysicalOrder>();
		long lastLoginTry = long.MinValue;
		long loginRetryTime = 10000; //milliseconds = 10 seconds.
		private bool isPositionUpdateComplete = false;
		private bool isOrderUpdateComplete = false;
		private string fixDestination = "MBT";
		
		public MBTFIXProvider(string name)
		{
			log.Notice("Using config file name: " + name);
			ProviderName = "MBTFIXProvider";
			if( name.Contains(".config")) {
				throw new ApplicationException("Please remove .config from config section name.");
			}
  			ConfigSection = name;
  			if( SyncTicks.Enabled) {
	  			HeartbeatDelay = int.MaxValue;
  			} else {
	  			HeartbeatDelay = 40;
  			}
  			FIXFilter = new MBTFIXFilter();
		}
		
		public override void OnDisconnect() {
			SendEndBroker();
		}

		public override void OnRetry() {
		}
		
		private void SendStartBroker() {
			lock( symbolsRequestedLocker) {
				foreach( var kvp in symbolsRequested) {
					SymbolInfo symbol = kvp.Value;
					long end = Factory.Parallel.TickCount + 2000;
					while( !receiver.OnEvent(symbol,(int)EventType.StartBroker,symbol)) {
						if( isDisposed) return;
						if( Factory.Parallel.TickCount > end) {
							throw new ApplicationException("Timeout while sending start broker.");
						}
						Factory.Parallel.Yield();
					}
				}
			}
		}
		
		private void SendEndBroker() {
			lock( symbolsRequestedLocker) {
				foreach(var kvp in symbolsRequested) {
					SymbolInfo symbol = kvp.Value;
					long end = Factory.Parallel.TickCount + 2000;
					while( !receiver.OnEvent(symbol,(int)EventType.EndBroker,symbol)) {
						if( isDisposed) return;
						if( Factory.Parallel.TickCount > end) {
							throw new ApplicationException("Timeout while sending end broker.");
						}
						Factory.Parallel.Yield();
					}
				}
			}
		}
		
		public override Yield OnLogin() {
			if( debug) log.Debug("Login()");
			
			if( lastLoginTry + loginRetryTime > Factory.Parallel.TickCount) {
				return Yield.NoWork.Repeat;
			}
			
			lastLoginTry = Factory.Parallel.TickCount;

			FixFactory = new FIXFactory4_4(1,UserName,fixDestination);
			var mbtMsg = FixFactory.Create();
			mbtMsg.SetEncryption(0);
			mbtMsg.SetHeartBeatInterval(30);
			mbtMsg.ResetSequence();
			mbtMsg.SetEncoding("554_H1");
			mbtMsg.SetPassword(Password);
			mbtMsg.AddHeader("A");
			if( debug) {
				log.Debug("Login message: \n" + mbtMsg);
			}
			SendMessage( mbtMsg);
			
			var end = Factory.Parallel.TickCount + 15 * 1000;
			Packet packet;
			while( !Socket.TryGetPacket(out packet)) {
				if( IsInterrupted) return Yield.NoWork.Repeat;
				Factory.Parallel.Yield();
				if( Factory.Parallel.TickCount > end) {
					FailLogin(mbtMsg.ToString());
				}
			}

			if( debug) log.Debug("Received FIX message: " + packet);
			if( !VerifyLogin(packet)) {
				RegenerateSocket();
				return Yield.DidWork.Repeat;
			}
			
			StartRecovery();
			
            return Yield.DidWork.Repeat;
        }
		
		private void Logout() {
			var mbtMsg = FixFactory.Create();
			log.Info("Logout message = " + mbtMsg);
			mbtMsg.AddHeader("5");
			SendMessage(mbtMsg);
		}
		
		protected override void OnStartRecovery()
		{
			isPositionUpdateComplete = false;
			isOrderUpdateComplete = false;
			if( !LogRecovery) {
				PacketFIXT1_1.IsQuietRecovery = true;
			}

			RequestOrders();
			
			RequestPositions();
		}
		
		public override void OnStartSymbol(SymbolInfo symbol)
		{
        	if( IsRecovered) {
				long end = Factory.Parallel.TickCount + 2000;
        		while( !receiver.OnEvent(symbol,(int)EventType.StartBroker,symbol)) {
        			if( IsInterrupted) return;
					if( Factory.Parallel.TickCount > end) {
						throw new ApplicationException("Timeout while sending start broker.");
					}
        			Factory.Parallel.Yield();
        		}
        	}
		}

		public override void OnStopSymbol(SymbolInfo symbol)
		{
			long end = Factory.Parallel.TickCount + 2000;
			while( !receiver.OnEvent(symbol,(int)EventType.EndBroker,symbol)) {
				if( IsInterrupted) return;
				if( Factory.Parallel.TickCount > end) {
					throw new ApplicationException("Timeout while sending stop broker.");
				}
				Factory.Parallel.Yield();
			}
		}
		

		private void RequestPositions() {
			var fixMsg = (FIXMessage4_4) FixFactory.Create();
			fixMsg.SetSubscriptionRequestType(1);
			fixMsg.SetAccount(AccountNumber);
			fixMsg.SetPositionRequestId(1);
			fixMsg.SetPositionRequestType(0);
			fixMsg.AddHeader("AN");
			SendMessage(fixMsg);
		}

		private void RequestOrders() {
			var fixMsg = (FIXMessage4_4) FixFactory.Create();
			fixMsg.SetAccount(AccountNumber);
			fixMsg.SetMassStatusRequestID(TimeStamp.UtcNow);
			fixMsg.SetMassStatusRequestType(90);
			fixMsg.AddHeader("AF");
			SendMessage(fixMsg);
		}
		
		private void SendHeartbeat() {
			var fixMsg = (FIXMessage4_4) FixFactory.Create();
			fixMsg.AddHeader("0");
			SendMessage( fixMsg);
		}
		
		private unsafe bool VerifyLogin(Packet packet) {
			PacketFIX4_4 packetFIX = (PacketFIX4_4) packet;
			if( !("A" == packetFIX.MessageType &&
				"FIX.4.4" == packetFIX.Version &&
				"MBT" == packetFIX.Sender && 
				UserName == packetFIX.Target && 
				"0" == packetFIX.Encryption && 
				30 == packetFIX.HeartBeatInterval) ) {
				StringBuilder message = new StringBuilder();
				message.AppendLine("Invalid login response:");
				message.AppendLine("  message type = " + packetFIX.MessageType);
				message.AppendLine("  version = " + packetFIX.Version);
				message.AppendLine("  sender = " + packetFIX.Sender);
				message.AppendLine("  target = " + packetFIX.Target);
				message.AppendLine("  encryption = " + packetFIX.Encryption);
				message.AppendLine("  heartbeat interval = " + packetFIX.HeartBeatInterval);
				message.AppendLine(packetFIX.ToString());
				log.Warn(message + " -- retrying.");
				return false;
			}
			return 1 == packetFIX.Sequence;
		}
		
		protected override void ReceiveMessage(Packet packet) {
			var packetFIX = (PacketFIX4_4) packet;
			switch( packetFIX.MessageType) {
				case "AP":
				case "AO":
					PositionUpdate( packetFIX);
					break;
				case "8":
					ExecutionReport( packetFIX);
					break;
				case "9":
					CancelRejected( packetFIX);
					break;
				case "1":
					SendHeartbeat();
					break;
				case "0":
					// Received heartbeat
					break;
				case "j":
					BusinessReject( packetFIX);
					break;
				case "h":
					log.Info("Ignoring packet: '" + packetFIX.MessageType + "'\n" + packetFIX);
					break;
				default:
					log.Warn("Ignoring packet: '" + packetFIX.MessageType + "'\n" + packetFIX);
					break;
			}
		}
		
		private void BusinessReject(PacketFIX4_4 packetFIX) {
			var lower = packetFIX.Text.ToLower();
			var text = packetFIX.Text;
			var errorOkay = false;
			errorOkay = lower.Contains("order") && lower.Contains("server") ? true : errorOkay;
			errorOkay = text.Contains("DEMOORDS") ? true : errorOkay;
			errorOkay = text.Contains("FXORD1") ? true : errorOkay;
			errorOkay = text.Contains("FXORD2") ? true : errorOkay;
			errorOkay = text.Contains("FXORD01") ? true : errorOkay;
			errorOkay = text.Contains("FXORD02") ? true : errorOkay;
			if( errorOkay) {
				log.Warn( packetFIX.Text + " -- Sending EndBroker event. \n" + packetFIX);
				SendEndBroker();
			} else {
				string message = "FIX Server reported an error: " + packetFIX.Text + "\n" + packetFIX;
				throw new ApplicationException( message);
			}
		}
		
		private void TryEndRecovery() {
			if( isPositionUpdateComplete && isOrderUpdateComplete) {
				isPositionUpdateComplete = false;
				isOrderUpdateComplete = false;
				if( !TryCancelRejectedOrders() ) {
					ReportRecovery();
					EndRecovery();
				}
			}
		}
		
		private bool isCancelingPendingOrders = false;
		
		private bool TryCancelRejectedOrders() {
			var pending = new List<PhysicalOrder>();
			lock( openOrdersLocker) {
				foreach( var kvp in openOrders) {
					PhysicalOrder order = kvp.Value;
					if( order.OrderState == OrderState.Pending && "ReplaceRejected".Equals(order.Tag)) {
						pending.Add(order);
					}
				}
			}
			if( pending.Count == 0) {
				isCancelingPendingOrders = false;
				return false;
			} else if( !isCancelingPendingOrders) {
				isCancelingPendingOrders = true;
				log.Info("Recovery completed with pending orders. Canceling them now..");
				foreach( var order in pending) {
					log.Info("Canceling Pending Order: " + order);
					OnCancelBrokerOrder(order.Symbol, order.BrokerOrder);
				}
			}
			return isCancelingPendingOrders;
		}

		private void ReportRecovery() {
			StringBuilder sb = new StringBuilder();
			lock( openOrdersLocker) {
				foreach( var kvp in openOrders) {
					PhysicalOrder order = kvp.Value;
					sb.Append( "    ");
					sb.Append( (string) order.BrokerOrder);
					sb.Append( " ");
					sb.Append( order);
					sb.AppendLine();
				}
			}
			log.Info("Recovered Open Orders:\n" + sb);
			SendStartBroker();
			PacketFIXT1_1.IsQuietRecovery = false;
		}
		
		private void PositionUpdate( PacketFIX4_4 packetFIX) {
			if( packetFIX.MessageType == "AO") {
				isPositionUpdateComplete = true;
				if(debug) log.Debug("PositionUpdate Complete.");
				TryEndRecovery();
			} else {
				var position = packetFIX.LongQuantity + packetFIX.ShortQuantity;
				SymbolInfo symbolInfo;
				try {
					symbolInfo = Factory.Symbol.LookupSymbol(packetFIX.Symbol);
				} catch( ApplicationException ex) {
					log.Error("Error looking up " + packetFIX.Symbol + ": " + ex.Message);
					return;
				}
				if( debug) log.Debug("PositionUpdate: " + symbolInfo + "=" + position);
				var orderHandler = GetAlgorithm(symbolInfo.BinaryIdentifier);
				orderHandler.SetActualPosition( position);
			}
		}
		
		private void ExecutionReport( PacketFIX4_4 packetFIX) {
			if( packetFIX.Text == "END") {
				isOrderUpdateComplete = true;
				if(debug) log.Debug("ExecutionReport Complete.");
				TryEndRecovery();
			} else {
				if( debug && (LogRecovery || !IsRecovery) ) {
					log.Debug("ExecutionReport: " + packetFIX);
				}
				PhysicalOrder order;
				string orderStatus = packetFIX.OrderStatus;
				switch( orderStatus) {
					case "0": // New
						SymbolInfo symbol = null;
						try {
							symbol = Factory.Symbol.LookupSymbol( packetFIX.Symbol);
						} catch( ApplicationException) {
							// symbol unknown.
						}
						if( symbol != null) {
							order = UpdateOrder( packetFIX, OrderState.Active, null);
							if( IsRecovered) {
								var algorithm = GetAlgorithm( symbol.BinaryIdentifier);
								algorithm.OnCreateBrokerOrder( order);
							}
						}
						break;
					case "1": // Partial
						UpdateOrder( packetFIX, OrderState.Active, null);
						if( IsRecovered) {
							SendFill( packetFIX);
						}
						break;
					case "2":  // Filled 
						if( IsRecovered) {
							SendFill( packetFIX);
						}
						order = RemoveOrder( packetFIX.ClientOrderId);
						if( IsRecovered) {
							var algorithm = GetAlgorithm( order.Symbol.BinaryIdentifier);
							algorithm.PerformCompare();
						}
						if( order != null && order.Replace != null) {
							if( debug) log.Debug( "Found this order in the replace property. Removing it also: " + order.Replace);
							RemoveOrder( order.Replace.BrokerOrder.ToString());
						}
						break;
					case "5": // Replaced
						order = ReplaceOrder( packetFIX, OrderState.Active, null);
						if( IsRecovered) {
							if( order != null) {
								var algorithm = GetAlgorithm( order.Symbol.BinaryIdentifier);
								algorithm.OnChangeBrokerOrder( order, packetFIX.OriginalClientOrderId);
							} else {
								log.Warn("Changing order status after cancel/replace failed. Probably due to already being canceled or filled. Ignoring.");
							}
						}
						break;
					case "4": // Canceled
						order = RemoveOrder( packetFIX.ClientOrderId);
						order = RemoveOrder( packetFIX.OriginalClientOrderId);
						if( IsRecovered) {
							if( order != null) {
								var algorithm = GetAlgorithm( order.Symbol.BinaryIdentifier);
								algorithm.OnCancelBrokerOrder( order.Symbol, packetFIX.ClientOrderId);
							} else if( IsRecovered) {
								log.Notice("Order " + packetFIX.ClientOrderId + " was already removed after cancel. Ignoring.");
							}
						}
						break;
					case "6": // Pending Cancel
						UpdateOrder( packetFIX, OrderState.Pending, "PendingCancel");
						TryHandlePiggyBackFill(packetFIX);
						break;
					case "8": // Rejected
						RejectOrder( packetFIX);
						break;
					case "9": // Suspended
						UpdateOrder( packetFIX, OrderState.Suspended, packetFIX);
						// Ignore 
						break;
					case "A": // PendingNew
						UpdateOrder( packetFIX, OrderState.Active, null);
						break;
					case "E": // Pending Replace
						UpdateOrder( packetFIX, OrderState.Pending, "PendingReplace");
						TryHandlePiggyBackFill(packetFIX);
						break;
					case "R": // Resumed.
						UpdateOrder( packetFIX, OrderState.Active, null);
						// Ignore
						break;
					default:
						throw new ApplicationException("Unknown order status: '" + orderStatus + "'");
				}
			}
		}

		private void TryHandlePiggyBackFill(PacketFIX4_4 packetFIX) {
			if( packetFIX.LastQuantity > 0 && IsRecovered) {
				SendFill( packetFIX);
			}
			if( packetFIX.LeavesQuantity == 0) {
				var order = RemoveOrder( packetFIX.ClientOrderId);
				if( packetFIX.OriginalClientOrderId != null) {
					order = RemoveOrder( packetFIX.OriginalClientOrderId);
				}
				if( IsRecovered) {
					var algorithm = GetAlgorithm( order.Symbol.BinaryIdentifier);
					algorithm.PerformCompare();
				}
			}
		}
		
		private void CancelRejected( PacketFIX4_4 packetFIX) {
			if( debug && (LogRecovery || !IsRecovery) ) {
				log.Debug("ExecutionReport: " + packetFIX);
			}
			string orderStatus = packetFIX.OrderStatus;
			switch( orderStatus) {
				case "8": // Rejected
					var rejectReason = false;
					rejectReason = packetFIX.Text.Contains("No such order") ? true : rejectReason;
					rejectReason = packetFIX.Text.Contains("Cancel request already pending") ? true : rejectReason;
					rejectReason = packetFIX.Text.Contains("ORDER in pending state") ? true : rejectReason;
					RemoveOrder( packetFIX.ClientOrderId);
					if( packetFIX.Text.Contains("No such order")) {
						RemoveOrder( packetFIX.OriginalClientOrderId);
					}
					if( !rejectReason && IsRecovered) {
						var message = "Order Rejected: " + packetFIX.Text + "\n" + packetFIX;
						var ignore = "The cancel reject error message '" + packetFIX.Text + "' was unrecognized. So it is being ignored. ";
						var handle = "If this reject causes any other problems please report it to have it added and properly handled.";
						log.Warn( message);
						log.Error( ignore + handle);
					} else {
						if( LogRecovery || !IsRecovery) {
							log.Info( "CancelReject(" + packetFIX.Text + ") Removed cancel order: " + packetFIX.ClientOrderId);
						}
					}
					break;
				default:
					throw new ApplicationException("Unknown cancel rejected order status: '" + orderStatus + "'");
			}
		}
		
		private bool GetLogicalOrderId( string clientOrderId, out int logicalOrderId) {
			logicalOrderId = 0;
			string[] parts = clientOrderId.Split(DOT_SEPARATOR);
			try {
				logicalOrderId = int.Parse(parts[0]);
			} catch( FormatException) {
				log.Warn("Fill received from order " + clientOrderId + " created externally. So it lacks any logical order id. That means a fill cannot be sent to the strategy. This will get resolved at next synchronization.");
				return false;
			}
			return true;
		}
		
		private int SideToSign( string side) {
			switch( side) {
				case "1": // Buy
					return 1;
				case "2": // Sell
				case "5": // SellShort
					return -1;
				default:
					throw new ApplicationException("Unknown order side: " + side);
			}
		}
		
		public void SendFill( PacketFIX4_4 packetFIX) {
			if( debug ) log.Debug("SendFill( " + packetFIX.ClientOrderId + ")");
			var symbolInfo = Factory.Symbol.LookupSymbol(packetFIX.Symbol);
			var timeZone = new SymbolTimeZone(symbolInfo);
			if( GetSymbolStatus(symbolInfo)) {
				var algorithm = GetAlgorithm(symbolInfo.BinaryIdentifier);
				var order = GetPhysicalOrder( packetFIX.ClientOrderId);
				var fillPosition = packetFIX.LastQuantity * SideToSign(packetFIX.Side);
				TimeStamp executionTime;
				if( UseLocalFillTime) {
					executionTime = TimeStamp.UtcNow;
				} else {
					executionTime = new TimeStamp(packetFIX.TransactionTime);
				}
				var configTime = executionTime;
				configTime.AddSeconds( timeZone.UtcOffset(executionTime));
				var fill = Factory.Utility.PhysicalFill(fillPosition,packetFIX.LastPrice,configTime,executionTime,order,false);
				if( debug) log.Debug( "Sending physical fill: " + fill);
	            algorithm.ProcessFill( fill,packetFIX.OrderQuantity,packetFIX.CumulativeQuantity,packetFIX.LeavesQuantity);
			}
		}
		
		public void ProcessFill( SymbolInfo symbol, LogicalFillBinary fill) {
			lock( openOrdersLocker) {
				if( debug) log.Debug("Sending fill event for " + symbol + " to receiver: " + fill);
				while( !receiver.OnEvent(symbol,(int)EventType.LogicalFill,fill)) {
					Factory.Parallel.Yield();
				}
			}
		}

		public Iterable<PhysicalOrder> GetActiveOrders(SymbolInfo symbol) {
			var result = new ActiveList<PhysicalOrder>();
			lock( openOrdersLocker) {
		        foreach( var kvp in openOrders) {
					var order = kvp.Value;
					if( order.Symbol == symbol) {
						result.AddLast(order);
					}
		        }
			}
			return result;
		}
		
		public void RejectOrder( PacketFIX4_4 packetFIX) {
			var rejectReason = false;
			rejectReason = packetFIX.Text.Contains("Outside trading hours") ? true : rejectReason;
			rejectReason = packetFIX.Text.Contains("not accepted this session") ? true : rejectReason;
			rejectReason = packetFIX.Text.Contains("Pending live orders") ? true : rejectReason;
			rejectReason = packetFIX.Text.Contains("Trading temporarily unavailable") ? true : rejectReason;
			rejectReason = packetFIX.Text.Contains("improper setting") ? true : rejectReason;			
			rejectReason = packetFIX.Text.Contains("No position to close") ? true : rejectReason;			
			RemoveOrder( packetFIX.ClientOrderId);
			RemoveOrder( packetFIX.OriginalClientOrderId);
			if( !rejectReason && IsRecovered) {
				var message = "Order Rejected: " + packetFIX.Text + "\n" + packetFIX;
				var ignore = "The reject error message '" + packetFIX.Text + "' was unrecognized. So it is being ignored. ";
				var handle = "If this reject causes any other problems please report it to have it added and properly handled.";
				log.Warn( message);
				log.Error( ignore + handle);
			} else {
				log.Info( "RejectOrder(" + packetFIX.Text + ") Removed cancel order: " + packetFIX.ClientOrderId + " and original order: " + packetFIX.OriginalClientOrderId);
			}
		}
		
		public PhysicalOrder GetOrderById( object brokerOrder) {
			PhysicalOrder order;
			lock( openOrdersLocker) {
				if( !openOrders.TryGetValue( (string) brokerOrder, out order)) {
					throw new ApplicationException("Unable to find order for id: " + brokerOrder);
				}
			}
			return order;
		}
		
		private static readonly char[] DOT_SEPARATOR = new char[] { '.' };
		public PhysicalOrder RemoveOrder( string clientOrderId) {
			if( string.IsNullOrEmpty(clientOrderId)) {
				return null;
			}
			lock( openOrdersLocker) {
				PhysicalOrder order = null;
				if( openOrders.TryGetValue(clientOrderId, out order)) {
					openOrders.Remove(clientOrderId);
					if( debug && (LogRecovery || !IsRecovery) ) {
						log.Debug("RemoveOrder( " + clientOrderId + ")");
					}
				}
				return order;
			}
		}	
		
		private OrderType GetOrderType(PacketFIX4_4 packetFIX) {
			var orderType = OrderType.None;
			switch( packetFIX.Side) {
				case "1":
					switch( packetFIX.OrderType) {
						case "1":
							orderType = OrderType.BuyMarket;
							break;
						case "2":
							orderType = OrderType.BuyLimit;
							break;
						case "3":
							orderType = OrderType.BuyStop;
							break;
						default:
							break;
					}
					break;
				case "2":
				case "5":
					switch( packetFIX.OrderType) {
						case "1":
							orderType = OrderType.SellMarket;
							break;
						case "2":
							orderType = OrderType.SellLimit;
							break;
						case "3":
							orderType = OrderType.SellStop;
							break;
						default:
							break;
					}
					break;
				default:
					throw new ApplicationException("Unknown order side: '" + packetFIX.Side + "'\n" + packetFIX);
			}
			return orderType;
		}

		private OrderSide GetOrderSide( PacketFIX4_4 packetFIX) {
			OrderSide side;
			switch( packetFIX.Side) {
				case "1":
					side = OrderSide.Buy;
					break;
				case "2":
					side = OrderSide.Sell;
					break;
				case "5":
					side = OrderSide.SellShort;
					break;
				default:
					throw new ApplicationException( "Unknown order side: " + packetFIX.Side + "\n" + packetFIX);
			}
			return side;
		}

		private int GetLogicalOrderId( PacketFIX4_4 packetFIX) {
			string[] parts = packetFIX.ClientOrderId.Split(DOT_SEPARATOR);
			int logicalOrderId = 0;
			try {
				logicalOrderId = int.Parse(parts[0]);
			} catch( FormatException) {
			}
			return logicalOrderId;
		}
		
		public PhysicalOrder UpdateOrder( PacketFIX4_4 packetFIX, OrderState orderState, object note) {
			var clientOrderId = packetFIX.ClientOrderId;
			if( !string.IsNullOrEmpty(packetFIX.OriginalClientOrderId)) {
			   	clientOrderId = packetFIX.OriginalClientOrderId;
			}
			if( debug && (LogRecovery || !IsRecovery) ) {
				log.Debug("UpdateOrder( " + clientOrderId + ", state = " + orderState + ")");
			}
			return UpdateOrReplaceOrder( packetFIX, clientOrderId, clientOrderId, orderState, note);
		}
		
		public PhysicalOrder ReplaceOrder( PacketFIX4_4 packetFIX, OrderState orderState, object note) {
			if( debug && (LogRecovery || !IsRecovery) ) {
				log.Debug("ReplaceOrder( " + packetFIX.OriginalClientOrderId + ", state = " + orderState + " => " + packetFIX.ClientOrderId + ")");
			}
			var order = UpdateOrReplaceOrder( packetFIX, packetFIX.OriginalClientOrderId, packetFIX.ClientOrderId, orderState, note);
			if( order != null) {
				RemoveOrder( packetFIX.OriginalClientOrderId);
			}
			return order;
		}
		
		public PhysicalOrder UpdateOrReplaceOrder( PacketFIX4_4 packetFIX, string clientOrderId, string newClientOrderId, OrderState orderState, object note) {
			SymbolInfo symbolInfo;
			try {
				symbolInfo = Factory.Symbol.LookupSymbol(packetFIX.Symbol);
			} catch( ApplicationException ex) {
				log.Error("Error looking up " + packetFIX.Symbol + ": " + ex.Message);
				return null;
			}
			PhysicalOrder oldOrder = null;
			long logicalSerialNumber = 0;
			try {
				oldOrder = GetOrderById( clientOrderId);
				logicalSerialNumber = oldOrder.LogicalSerialNumber;
			} catch( ApplicationException) {
				if( !IsRecovery ) {
					log.Warn("Order ID# " + clientOrderId + " was not found for update or replace.");
					return null;
				}			
			}
			int quantity = packetFIX.LeavesQuantity;
			PhysicalOrder order;
			var type = GetOrderType( packetFIX);
			var side = GetOrderSide( packetFIX);
			var logicalId = GetLogicalOrderId( packetFIX);
			order = Factory.Utility.PhysicalOrder(orderState, symbolInfo, side, type, packetFIX.Price, packetFIX.LeavesQuantity, logicalId, logicalSerialNumber, newClientOrderId, null);
			if( quantity > 0) {
				if( info && (LogRecovery || !IsRecovery) ) {
					if( debug) log.Debug("Updated order: " + order + ".  Executed: " + packetFIX.CumulativeQuantity + " Remaining: " + packetFIX.LeavesQuantity);
				}
			} else {
				if( info && (LogRecovery || !IsRecovery) ) {
					if( debug) log.Debug("Order Completely Filled. Id: " + packetFIX.ClientOrderId + ".  Executed: " + packetFIX.CumulativeQuantity);
				}
			}
			lock( openOrdersLocker) {
				openOrders[newClientOrderId] = order;
			}
			if( trace) {
				log.Trace("Updated order list:");
				lock( openOrdersLocker) {
					foreach( var kvp in openOrders) {
						PhysicalOrder temp = kvp.Value;
						log.Trace( "    " + temp.BrokerOrder + " " + temp);
					}
				}
			}
			return order;
		}

		private void TestMethod(PacketFIX4_4 packetFIX) {
			string account = packetFIX.Account;
			string destination = packetFIX.Destination;
			int orderQuantity = packetFIX.OrderQuantity;
			double averagePrice = packetFIX.AveragePrice;
			string orderID = packetFIX.OrderId;
			string massStatusRequestId = packetFIX.MassStatusRequestId;
			string positionEffect = packetFIX.PositionEffect;
			string orderType = packetFIX.OrderType;
			string clientOrderId = packetFIX.ClientOrderId;
			double price = packetFIX.Price;
			int cumulativeQuantity = packetFIX.CumulativeQuantity;
			string executionId = packetFIX.ExecutionId;
			int productType = packetFIX.ProductType;
			string symbol = packetFIX.Symbol;
			string side = packetFIX.Side;
			string timeInForce = packetFIX.TimeInForce;
			string executionType = packetFIX.ExecutionType;
			string internalOrderId = packetFIX.InternalOrderId;
			string transactionTime = packetFIX.TransactionTime;
			int leavesQuantity = packetFIX.LeavesQuantity;
		}
		
		private void OnException( Exception ex) {
			// Attempt to propagate the exception.
			log.Error("Exception occurred", ex);
			SendError( ex.Message + "\n" + ex.StackTrace);
			Dispose();
		}
		
		private PhysicalOrder GetPhysicalOrder( string clientOrderId) {
			PhysicalOrder origOrder;
			lock( openOrdersLocker) {
				if( openOrders.TryGetValue(clientOrderId, out origOrder)) {
				   	return origOrder;
				} else {
					throw new ApplicationException("Can't find client order id " + clientOrderId + " in list.");
				}
			}
		}
		
		private OrderAlgorithm GetAlgorithm(string clientOrderId) {
			PhysicalOrder origOrder;
			try {
				origOrder = GetOrderById(clientOrderId);
			} catch( ApplicationException) {
				throw new ApplicationException("Unable to find physical order by client id: " + clientOrderId);
			}
			return GetAlgorithm( origOrder.Symbol.BinaryIdentifier);
		}
		
		private OrderAlgorithm GetAlgorithm(long symbol) {
			OrderAlgorithm algorithm;
			lock( orderAlgorithmLocker) {
				if( !orderAlgorithms.TryGetValue(symbol, out algorithm)) {
					var symbolInfo = Factory.Symbol.LookupSymbol(symbol);
					algorithm = Factory.Utility.OrderAlgorithm( "mbtfix", symbolInfo, this);
					orderAlgorithms.Add(symbol,algorithm);
					algorithm.OnProcessFill = ProcessFill;
				}
			}
			return algorithm;
		}
		
		private bool RemoveOrderHandler(long symbol) {
			lock( orderAlgorithmLocker) {
				if( orderAlgorithms.ContainsKey(symbol)) {
					orderAlgorithms.Remove(symbol);
					return true;
				} else {
					return false;
				}
			}
		}
		
		public override void PositionChange(Receiver receiver, SymbolInfo symbol, int desiredPosition, Iterable<LogicalOrder> inputOrders)
		{
			if( !IsRecovered) {
				if( HasFirstRecovery) {
					log.Warn("PositionChange event received while FIX was offline or recovering. Current connection status is: " + ConnectionStatus);
					return;
				} else {
					throw new ApplicationException("PositionChange event received prior to completing FIX recovery. Current connection status is: " + ConnectionStatus);
				}
			}
			var count = inputOrders == null ? 0 : inputOrders.Count;
			if( debug) log.Debug( "PositionChange " + symbol + ", desired " + desiredPosition + ", order count " + count);
			
			var algorithm = GetAlgorithm(symbol.BinaryIdentifier);
			algorithm.SetDesiredPosition(desiredPosition);
			algorithm.SetLogicalOrders(inputOrders);

			CompareLogicalOrders(symbol);
			
			var tickSync = SyncTicks.GetTickSync(symbol.BinaryIdentifier);
			if( SyncTicks.Enabled ) {
				tickSync.RemovePositionChange();
			}				
		}
		
		private void CompareLogicalOrders(SymbolInfo symbol) {
			var algorithm = GetAlgorithm(symbol.BinaryIdentifier);
			lock( orderAlgorithmLocker) {
    			algorithm.PerformCompare();
			}
		}
		
	    protected override void Dispose(bool disposing)
	    {
	    	base.Dispose(disposing);
           	nextConnectTime = Factory.Parallel.TickCount + 10000;
	    }    
	        
		private int GetLogicalOrderId(int physicalOrderId) {
        	int logicalOrderId;
        	if( physicalToLogicalOrderMap.TryGetValue(physicalOrderId,out logicalOrderId)) {
        		return logicalOrderId;
        	} else {
        		return 0;
        	}
		}
		Dictionary<int,int> physicalToLogicalOrderMap = new Dictionary<int, int>();
	        
		public void OnCreateBrokerOrder(PhysicalOrder physicalOrder)
		{
			physicalOrder.OrderState = OrderState.Pending;
			if( debug) log.Debug( "OnCreateBrokerOrder " + physicalOrder);
			OnCreateOrChangeBrokerOrder(physicalOrder,null, false);
		}
	        
		private void OnCreateOrChangeBrokerOrder(PhysicalOrder physicalOrder, object origBrokerOrder, bool isChange)
		{
			var fixMsg = (FIXMessage4_4) FixFactory.Create();
			lock( openOrdersLocker) {
				openOrders[(string)physicalOrder.BrokerOrder] = physicalOrder;
			}
			
			if( debug) log.Debug( "Adding Order to open order list: " + physicalOrder);
			if( isChange) {
				fixMsg.SetClientOrderId((string)physicalOrder.BrokerOrder);
				fixMsg.SetOriginalClientOrderId((string)origBrokerOrder);
				var origOrder = GetPhysicalOrder( (string) origBrokerOrder);
				if( origOrder != null) {
					origOrder.Replace = physicalOrder;
					if( debug) log.Debug("Setting replace property of " + origBrokerOrder + " to " + physicalOrder.BrokerOrder);
				}
			} else {
				fixMsg.SetClientOrderId((string)physicalOrder.BrokerOrder);
			}
			fixMsg.SetAccount(AccountNumber);
			if( isChange) {
				fixMsg.AddHeader("G");
			} else {
				fixMsg.AddHeader("D");
				if( physicalOrder.Symbol.Destination.ToLower() == "default") {
					fixMsg.SetDestination("MBTX");
				} else {
					fixMsg.SetDestination(physicalOrder.Symbol.Destination);
				}
			}
			fixMsg.SetHandlingInstructions(1);
			fixMsg.SetSymbol(physicalOrder.Symbol.Symbol);
			fixMsg.SetSide( GetOrderSide(physicalOrder.Side));
			switch( physicalOrder.Type) {
				case OrderType.BuyLimit:
					fixMsg.SetOrderType(2);
					fixMsg.SetPrice(physicalOrder.Price);
					fixMsg.SetTimeInForce(1);
					break;
				case OrderType.BuyMarket:
					fixMsg.SetOrderType(1);
					fixMsg.SetTimeInForce(0);
					break;
				case OrderType.BuyStop:
					fixMsg.SetOrderType(3);
					fixMsg.SetPrice(physicalOrder.Price);
					fixMsg.SetStopPrice(physicalOrder.Price);
					fixMsg.SetTimeInForce(1);
					break;
				case OrderType.SellLimit:
					fixMsg.SetOrderType(2);
					fixMsg.SetPrice(physicalOrder.Price);
					fixMsg.SetTimeInForce(1);
					break;
				case OrderType.SellMarket:
					fixMsg.SetOrderType(1);
					fixMsg.SetTimeInForce(0);
					break;
				case OrderType.SellStop:
					fixMsg.SetOrderType(3);
					fixMsg.SetPrice(physicalOrder.Price);
					fixMsg.SetStopPrice(physicalOrder.Price);
					fixMsg.SetTimeInForce(1);
					break;
			}
			fixMsg.SetLocateRequired("N");
			fixMsg.SetTransactTime(TimeStamp.UtcNow);
			fixMsg.SetOrderQuantity((int)physicalOrder.Size);
			fixMsg.SetOrderCapacity("A");
			fixMsg.SetUserName();
			if( isChange) {
				if( debug) log.Debug("Change order: \n" + fixMsg);
			} else {
				if( debug) log.Debug("Create new order: \n" + fixMsg);
			}
			SendMessage( fixMsg);
		}

		private int GetOrderSide( OrderSide side) {
			switch( side) {
				case OrderSide.Buy:
					return 1;
				case OrderSide.Sell:
					return 2;
				case OrderSide.SellShort:
					return 5;
				case OrderSide.SellShortExempt:
					return 6;
				default:
					throw new ApplicationException("Unknown OrderSide: " + side);
			}
		}
		

		private long GetUniqueOrderId() {
			return TimeStamp.UtcNow.Internal;
		}
		
		public void OnCancelBrokerOrder(SymbolInfo symbol, object origBrokerOrder)
		{
			PhysicalOrder physicalOrder;
			try {
				physicalOrder = GetOrderById( origBrokerOrder);
			} catch( ApplicationException ex) {
				log.Warn("Order probably already canceled. " + ex.Message);
				if( SyncTicks.Enabled) {
					var tickSync = SyncTicks.GetTickSync(symbol.BinaryIdentifier);
					tickSync.RemovePhysicalOrder();
				}
				return;
			}
			physicalOrder.OrderState = OrderState.Pending;
			if( debug) log.Debug( "OnCancelBrokerOrder " + physicalOrder);
			
			var fixMsg = (FIXMessage4_4) FixFactory.Create();
			string newClientOrderId = physicalOrder.LogicalOrderId + "." + GetUniqueOrderId();
			fixMsg.SetOriginalClientOrderId((string)origBrokerOrder);
			fixMsg.SetClientOrderId(newClientOrderId);
			fixMsg.SetAccount(AccountNumber);
			fixMsg.SetSide( GetOrderSide(physicalOrder.Side));
			fixMsg.AddHeader("F");
			fixMsg.SetSymbol(physicalOrder.Symbol.Symbol);
			fixMsg.SetTransactTime(TimeStamp.UtcNow);
			SendMessage(fixMsg);
		}
		
		public void OnChangeBrokerOrder(PhysicalOrder physicalOrder, object origBrokerOrder)
		{
			physicalOrder.OrderState = OrderState.Pending;
			if( debug) log.Debug( "OnChangeBrokerOrder( " + physicalOrder + ")");
			OnCreateOrChangeBrokerOrder( physicalOrder, origBrokerOrder, true);
		}
	}
}