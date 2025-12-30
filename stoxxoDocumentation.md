MULTI LEG OPTIONS EXECUTION FROM CHARTING PLATFORMS
(SUCH AS AMIBROKER, METATRADER, NINJATRADER, TRADINGVIEW AND HTTP (using PYTHON / JAVA / .NET / EXCEL etc)

Bridge Supports Multi Leg Execution from Charting platforms such as AmiBroker, MetaTrader, Ninja etc using provided plugins with a very simple single function call.

Below are the supported functions from the plugins, you can call according to your requirements. Few plugins may have different locations for parameters.

If you wish you have your own settings for the Multi Leg Execution, then you can create your own multi leg options structure. Click Here to read more.

VERY IMPORTANT FOR THE CUSTOM PROGRAMMERS ONLY WHO WISH TO ACCESS OVER HTTP USING ANY PROGRAMING LANGUAGE BE IT PYTHON / JAVA / .NET OR ANYTHING ELSE:

As mentioned in the above point “related to communicating with the Web Server‘’, the root url and the path will be derived in the same way. For more details about the root URL, you can refer to the above section.

For the Calls related to the Positions / Order Book etc, you can refer to the above document for related functions.

The best thing about HTTP Get is that you can even try calling the get URL using any browser such as Chrome and check for the response of an expected input. This will be very helpful in your coding.

Root URL: this will be your Web Server running url Eg http://localhost or http://localhost:21000

Python communication will be based on simple HTTP Get / Post requests. 
Path: this will be function name excluding IB_ 
Example for 	/PlaceMultiLegOrder
		/PlaceMultiLegOrderAdv
		/ExitMultiLegOrderByDetails
		/ExitMultiLegOrder
		/CombinedPremium

Special Care for Percentage Values: Many fields in Stoxxo support values in Points or Percentage. Points can be sent easily using Query String, however however for percentage you can use P. Example for Trailing Target 0.10P

Parameter : Please refer to the function signature for field details, just of ease, below fields can be used with particular function





If you’re using TradingView, then you can Click Here to read more.

string IB_PlaceMultiLegOrder (string OptionPortfolioName, string StrategyTag, string Symbol, string Product, int Lots, int NoDuplicateOrderForSeconds, string PortLegs);
This function can be used to place multi leg orders by providing bare minimum required mandatory fields. Details of each field are described in the table below.

If the function is successful then this will return the name of Portfolio which can be used to Exit the portfolio.

PortLegs is an optional parameter which can be used to specify the legs. Refer below for more details.

HTTP Get Sample Call For a Short Straddle Entry:
http://localhost:21000/PlaceMultiLegOrder?OptionPortfolioName=ShortStraddle&StrategyTag=Default&Symbol=NIFTY&Product=NRML&Lots=1&NoDuplicateOrderForSeconds=10

string IB_PlaceMultiLegOrderAdv (string OptionPortfolioName, string StrategyTag, string Symbol, string Product, string CombinedProfit, string CombinedLoss, string LegTarget, string LegSL, int Lots, int NoDuplicateOrderForSeconds, float EntryPrice, int SLtoCost, string PortLegs);
This function can be used to place multi leg orders by providing other fields as well like Combined SL, target etc.

It is Important to remember that Target / SL etc are String because you can send “5” or “5%” in those fields.

Suppose you want to send CombinedLoss but don’t want to send other fields like CombinedProfit, LegSL etc then you can send as “0”. Any optional parameters can be sent as default values. For string it is “” and for numeric you can pass 0.

If the function is successful then this will return the name of Portfolio which can be used to Exit the portfolio.

PortLegs is an optional parameter which can be used to specify the legs. Refer below for more details.

HTTP Get Sample Call For a Short Straddle Entry:
http://localhost:21000/PlaceMultiLegOrderAdv?OptionPortfolioName=ShortStraddle&StrategyTag=Default&Symbol=NIFTY&Product=NRML&CombinedProfit=0&CombinedLoss=5000&LegTarget=0&LegSL=5P&Lots=1&NoDuplicateOrderForSeconds=10&EntryPrice=0&SLtoCost=Yes




HTTP Get Sample Call For a Portfolio with custom leg details:
http://localhost:21000/PlaceMultiLegOrderAdv?OptionPortfolioName=ShortStraddle&StrategyTag=Default&Symbol=NIFTY&Product=NRML&CombinedProfit=0&CombinedLoss=5000&LegTarget=0&LegSL=5P&Lots=1&NoDuplicateOrderForSeconds=10&EntryPrice=0&SLtoCost=Yes&PortLegs=Strike:20000|Txn:SELL|Ins:CE|Expiry:CW|Lots:2||Strike:20000|Txn:SELL|Ins:PE|Expiry:CW|Lots:2

string IB_PlaceMultiLegOrderAdv (string OptionPortfolioName, string StrategyTag, string Symbol, string Product, string CombinedProfit, string CombinedLoss, string LegTarget, string LegSL, int Lots, int NoDuplicateOrderForSeconds, float EntryPrice, int SLtoCost, string PortLegs, int StartSeconds, int EndSeconds, int SqOffSeconds);
This function is similar to the above mentioned IB_PlaceMultiLegOrderAdv function with 3 additional parameters. By specifying these parameters, you can set the relevant time of the portfolio.

StartSeconds
EndSeconds
SqOffSeconds.

Suppose If you want to set only Start time by leaving the other 2 then you can send the other 2 as 0.

It is Important to send in seconds Eg. to send a time of 09:15:00 you need to send 33300.

HTTP Get Sample Call For a Portfolio with custom leg details:
http://localhost:21000/PlaceMultiLegOrderAdv?OptionPortfolioName=ShortStraddle&StrategyTag=Default&Symbol=NIFTY&Product=NRML&CombinedProfit=0&CombinedLoss=5000&LegTarget=0&LegSL=5P&Lots=1&NoDuplicateOrderForSeconds=10&EntryPrice=0&SLtoCost=Yes&PortLegs=Strike:20000|Txn:SELL|Ins:CE|Expiry:CW|Lots:2||Strike:20000|Txn:SELL|Ins:PE|Expiry:CW|Lots:2&StartSeconds=33600&EndSeconds=54900&SqOffSeconds=55500

string IB_GetLegs (string OptionPortfolioName);
This function can be used to get all the legs of the portfolio as a Pipeline separated values with each leg details separated by a tilde (~). Headers of the returned values are given below.

SNO | LegID | IsIdle | Symbol | Expiry (dd-MMM-yyyy)  | Strike | Instrument (CE / PE / FUT) | Txn (Buy / Sell) | Lot | Wait and Trade | Target Value | Sl value | IV | Delta | Theta | Vega

Important Notes
This function will return all the legs of the Portfolio despite their status or execution condition.
Greeks will be zero for the non active legs (legs which were not executed by any of the users).
Greeks values will be sent as per “Greeks, Multiply by Lots” settings.
Any % sign will be sent as P Eg SL of 20% will be sent as 20P.
Leg ID will be 0 for any leg which was not sent for the execution.
These values can be used to manage the Portfolio legs.


HTTP Get Sample Call:
http://localhost:21000/GetLegs?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

Sample Response:
{"status":"success","response": "1|16609|False|NIFTY|16-Dec-2025|26500|CE|SELL|2|0|0|0|0|0|0|0~2|16610|False|NIFTY|16-Dec-2025|26000|PE|SELL|2|0|0|0|0|0|0|0~3|16611|False|NIFTY|16-Dec-2025|27000|CE|BUY|2|-5P|10|20|0|0|0|0~4|16612|False|NIFTY|16-Dec-2025|26500|CE|SELL|2|0|0|0|0|0|0|0~5|16613|False|NIFTY|16-Dec-2025|27000|CE|BUY|2|0|10|20|0|0|0|0~6|16614|False|NIFTY|16-Dec-2025|27000|CE|BUY|2|-5P|10|20|0|0|0|0", "error": ""}

string IB_GetUserLegs (string OptionPortfolioName, string User, bool OnlyActiveLegs);
OptionPortfolioName (Mandatory)
User (Optional): If Blank then first user of the portfolio will be used as a User
OnlyActiveLegs (Optional): If you want to fetch only active legs (Legs where net remaining qty is more than 0).  The default is false.
This function can be used to get all the legs of a user of  the portfolio as a Pipeline separated values with each leg details separated by a tilde (~). Headers of the returned values are given below.

SNO | LegID | Symbol | Expiry (dd-MMM-yyyy)  | Strike | Instrument (CE / PE / FUT) | Txn (Buy / Sell) | Lot | Target Value | Sl value | IV | Delta | Theta | Vega | LTP | PNL | PNL Per Lot | Entry Filled Qty | Avg Entry Price | Exit Filled Qty | Avg Exit Price | Status | Target | SL | Locked Tgt | Trail SL

Important Notes
This function will return all the legs of the Portfolio which belongs to the specified user. If the user wasn’t specified then the first user of the portfolio will be used.
Greeks will be zero for the non active legs.
Greeks values will be sent as per “Greeks, Multiply by Lots” settings.
Any % sign will be sent as P Eg SL of 20% will be sent as 20P.


HTTP Get Sample Call:
http://localhost:21000/GetUserLegs?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

In the above call, we haven’t specified the user, so stoxxo will use the first active user portfolio.

Sample Response:
{"status":"success","response": "1|16609|NIFTY|16-Dec-2025|26500|CE|SELL|2|0|0|0|0|0|0|0|-90.00|0|150|3.20|150|3.80|Completed|0|0|0|02|16610|NIFTY|16-Dec-2025|26000|PE|SELL|2|0|0|0|0|0|0|0|-15105.00|0|150|49.30|150|150.00|Completed|0|0|0|04|16612|NIFTY|16-Dec-2025|26500|CE|SELL|2|0|0|0|0|0|0|0|0|0|150|3.80|150|3.80|Completed|0|0|0|05|16613|NIFTY|16-Dec-2025|27000|CE|BUY|2|10|20|0|0|0|0|0|0|0|150|1.15|150|1.15|Completed|11.15|0.05|0|0.0", "error": ""}

Other HTTP Get Sample Calls:
http://localhost:21000/GetUserLegs?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&User=SIM1

http://localhost:21000/GetUserLegs?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&User=SIM1&OnlyActiveLegs=true


bool IB_AddLeg (string OptionPortfolioName, string Leg);
This function can be used to add a new leg into the existing portfolio which is under execution by specifying the name returned by the PlaceMultiLegOrder Function and the leg details.
This can add one leg at a time. So if you want to add 2 legs then you need to call the function twice. 
This can add legs only into an existing under-executing portfolio. If the portfolio will have more than 1 user, then it will add the leg into all the users
For the leg format you can refer to ‘Leg Details for Multi-Leg calls’.
Few HTTP Get Sample Calls (Ensure about the correct portfolio name as returned from Place function call):
http://localhost:21000/AddLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Strike:26500|Txn:SELL|Ins:CE|Expiry:CW|Lots:2
http://localhost:21000/AddLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Strike:27000|Txn:BUY|Ins:CE|Expiry:CW|Lots:2|Target:Premium:10|SL:Premium:20
http://localhost:21000/AddLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Strike:27000|Txn:BUY|Ins:CE|Expiry:CW|Lots:2|Hedge:YES|WT:-5%|Target:Premium:10|SL:Premium:20

bool IB_SqOffLeg (string OptionPortfolioName, string Leg);
This function can be used for SqOff a leg which is under execution by specifying the name returned by the PlaceMultiLegOrder Function and the leg identification details.
This can SqOff one or more legs depending upon its identification details.
Leg Identification Details
You can identify a leg using different conditions which are described below.
SNO Select the leg using its serial number. 
Eg. SNO:1 to select the first leg of the portfolio
INS: Select the leg using its instrument type (CE / PE / FUT)
Eg. INS:CE (Select all the CE Legs of the Portfolio)
STRIKE: Select the leg using its Strike
Eg. STRIKE:25000 (Select all the 25000 strike Legs of the Portfolio)
Following Combinations can also be sent.
Txn:SELL|Ins:CE (Select all SELL CE legs of the portfolio)
Strike:20000|Txn:SELL|Ins:CE (Select all SELL CE legs having strike of 20000 of the portfolio)
Strike:ATM|Txn:SELL|Ins:CE (Select all SELL CE legs of ATM of the portfolio. ATM at the time of execution of the leg)
Strike:20000|Txn:SELL|Ins:CE|Expiry:11-Jan-2024 (Select all SELL CE legs having strike of 20000 and expiry of 11th Jan 24 of the portfolio)

HTTP GET Sample Calls
To SqOff All the CE Legs: http://localhost:21000/SqOffLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Ins:CE

To SqOff All the 26000 Strike Legs: 
http://localhost:21000/SqOffLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Strike:26000

To SqOff All the Sell CE Legs: 
http://localhost:21000/SqOffLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Txn:SELL|Ins:CE

To SqOff All the Legs as per following details: 
http://localhost:21000/SqOffLeg?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Leg=Strike:26500|Txn:SELL|Ins:CE|Expiry:CW|Lots:2

bool IB_ExecuteIdleLeg (string OptionPortfolioName, string Leg);
This function can be used to Execute a Idle leg of the executing portfolio by specifying the name returned by the PlaceMultiLegOrder Function and the leg identification details.
This can Execute one or more legs depending upon its identification details. This can execute only a Idle leg.
You need to send Leg Identification details as mentioned here.

bool IB_PartEntry (string OptionPortfolioName, string Leg, string Qty);
This function can be used for Part Entry for the whole Portfolio or for any particular leg by specifying the name returned by the PlaceMultiLegOrder Function.
Part Entry will only be taken in the active legs only. Legs which were completed or rejected will not be impacted.
OptionPortfolioName: Specify the Name returned by the PlaceMultiLegOrder Function
Leg: Optional, If you want the Portfolio level part entry, then send this as empty (“”) or null string.
If you want to take Part Entry in a specific leg, then send the Leg Identification details as mentioned here.
Qty: Quantity for the Part Entry. Qty can be sent as lots or in percentage eg 2 or 20%.

HTTP GET Sample Calls
To Increase the 50% Quantity into the CE Leg: 
http://localhost:21000/PartEntry?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Qty=50P&Leg=Ins:CE

To Increase the 100% Quantity into the all active Leg: 
http://localhost:21000/PartEntry?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Qty=100P

bool IB_PartExit (string OptionPortfolioName, string Leg, string Qty);
This function can be used for Part Exit for the whole Portfolio or for any particular leg by specifying the name returned by the PlaceMultiLegOrder Function.
OptionPortfolioName: Specify the Name returned by the PlaceMultiLegOrder Function
Leg: Optional, If you want the Portfolio level part exit, then send this as empty (“”) or null string.
If you want to take Part Exit in a specific leg, then send the Leg Identification details as mentioned here.
Qty: Quantity for the Part Exit. Qty can be sent as lots or in percentage eg 2 or 20%.
	HTTP GET Sample Calls
To Decrease the 50% Quantity into the CE Leg: 
http://localhost:21000/PartExit?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Qty=50P&Leg=Ins:CE

To Increase the 30% Quantity into the all active Leg: 
http://localhost:21000/PartExit?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&Qty=30P

bool IB_ModifyPortfolio (string OptionPortfolioName, string OptField, string Data, string Leg);
This function can be used to modify a Portfolio of any state. This doesn’t need an executing portfolio. Currently we support a few fields to be modified. However in the future we may increase more fields.
OptionPortfolioName (Mandatory): Specify the Name returned by the PlaceMultiLegOrder Function
OptField (Mandatory): Name of the field whose value needs to be modified. Currently Following fields can be modified using this function
CombinedSL, CombinedTgt, LegSL, LegTgt
Data (Mandatory): Value for the field. This should be a valid value as per the requirement of the field and be sent in Points or in percentage eg 2 or 20%.
Leg: If you want to modify the Leg level field then send the Leg Identification details as mentioned here.
In case of Leg Level fields, it's a mandatory field, else optional.
HTTP GET Sample Calls
Modify the Combined SL Value to 20%: 
http://localhost:21000/ModifyPortfolio?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&OptField=CombinedSL&Data=20P

Modify the Combined Target Value to 30%: 
http://localhost:21000/ModifyPortfolio?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&OptField=CombinedTgt&Data=30P

Modify all the CE Leg(s) SL Value to 30: 
http://localhost:21000/ModifyPortfolio?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&OptField=LegSL&Data=30&Leg=INS:CE


bool IB_ExitMultiLegOrderByDetails (string OptionPortfolioName, string StrategyTag, string Symbol, string Product, int Lots);
This function can be used to exit the multi leg portfolio which you executed.
It is Important to note that this exit function works as FIFO (First In First Out). This means suppose you sent 2 multi leg ShortStraddle orders for NIFTY and now you are sending exit then on first call this will exit the first portfolio.

HTTP Get Sample Call For the fired Short Straddle Exit
http://localhost:21000/ExitMultiLegOrderByDetails?OptionPortfolioName=ShortStraddle&StrategyTag=Default&Symbol=NIFTY&Product=NRML&Lots=1

bool IB_ExitMultiLegOrder (string OptionPortfolioName);
This function can be used to exit the portfolio by specifying the name returned by the PlaceMultiLegOrder Function.
This function will exit the same portfolio whose name will be provided. This is to note that order should be placed using PlaceMultiLegOrder Function.
float IB_CombinedPremium (string OptionPortfolioName);
This function returns the current combined premium of the multi leg options portfolio. You can even fetch combined premiums for the portfolios which are pending under execution but there should be an existing portfolio with that name.
HTTP GET Sample Calls
http://localhost:21000/CombinedPremium?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

string IB_PortfolioStatus (string OptionPortfolioName);
This function returns the current status of the Portfolio by Specifying the Name returned by the PlaceMultiLegOrder Function
Possible values for the Status are:
Disabled,
Stopped,
Pending,
Monitoring,
Started,
UnderExecution,
Failed,
Rejected,        
Completed,
UnderExit

HTTP GET Sample Calls
http://localhost:21000/PortfolioStatus?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

float IB_PortfolioMTM (string OptionPortfolioName, string User);
User (optional): User id of the user for which you wish to fetch MTM. If left blank then the first active user of the portfolio will be used.
This function returns the MTM of the first / selected user of the Portfolio by Specifying the Name returned by the PlaceMultiLegOrder Function.
HTTP GET Sample Calls
To Fetch the MTM of First active user: 
http://localhost:21000/PortfolioMTM?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

To Fetch the MTM of Specified User: 
http://localhost:21000/PortfolioMTM?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&User=SIM1

string IB_PortfolioLegs (string OptionPortfolioName, string All);
This function returns the Leg(s) of the first user of the Portfolio by Specifying the Name returned by the PlaceMultiLegOrder Function.
OptionPortfolioName: Specify the Name returned by the PlaceMultiLegOrder Function
All: Optional (Default Yes), If you want only active legs of the Portfolio, then you can set this as No /  False.
Leg details will be provided in below format, whereas more than one leg will be concatenated with ||  (double pipes)
Leg Sno|Strike|Ins|Txn|Remaining Qty|Status|Entry Filled Qty|Avg Entry Price|Exit Filled Qty|Avg Exit Price|PNL|Exit Type|Leg Remarks
Leg Remarks will contain the reason why the leg was executed Eg. ReExecuteLeg On OnSL of Leg: 1 (14322). However remarks won’t be there for the original legs of the portfolio.
Similarly if Leg was exited then the Exit Type will inform the exit reason like OnSL etc.

string IB_MultiLegOrderBook (string OptionPortfolioName, string User, string IgnoreRejected);
This function returns the Complete Multi Leg Order Book for the Specified Portfolio Name / User or the Complete Order Book if nothing is specified.
OptionPortfolioName (Optional) : If you want to fetch the Order Book for a specific portfolio, then specify the name here.
User (Optional): If you want to fetch the Order for a specified user, then specify here.
IgnoreRejected(Optional): The Default is True. If true then Rejected and Cancelled orders will not be sent in response.
This function can be used to get all the orders of a user of  the portfolio as a Pipeline separated values with each order details separated by a tilde (~). Headers of the returned values are given below.
If No user and Portfolio name was specified then the complete order book will be supplied.

Important: Do not use | and ~ characters in the Portfolio Names.

Portfolio Name | Leg ID | Exchange (NFO / BFO / MCX) | Symbol (NIFTY 16TH DEC 26500 CE)  | Product (MIS / NRML)  | Order Type (MARKET / LIMIT / SL / SLM)  | Order ID | Time (dd-MMM-yyyy HH:MM:SS)  | Txn(Buy/Sell) | Qty | Filled Qty | Pending Qty | Exchg Time (dd-MMM-yyyy HH:MM:SS)  | Avg Price | Status | LIMIT Price | Trigger Price | Order Failed (true / false) | User ID | Remarks | Tag | Is Hedge (true / false)

HTTP GET Sample Calls
To Fetch all the Orders for a specified Portfolio: 
http://localhost:21000/MultiLegOrderBook?OptionPortfolioName=HTTP_SHORTSTRADDLE_1

Sample Response:
{"status": "success", "response": "HTTP_SHORTSTRADDLE_1|16616|NFO|NIFTY 16TH DEC 26500 CE|NRML|MARKET|202010076721|13-Dec-2025 16:07:03|SELL|150|150|0|13-Dec-2025 16:07:03|3.20|COMPLETE|0|0|False|SIM1||10076719|No~HTTP_SHORTSTRADDLE_1|16617|NFO|NIFTY 16TH DEC 26000 PE|NRML|MARKET|202010076725|13-Dec-2025 16:07:03|SELL|150|150|0|13-Dec-2025 16:07:03|49.30|COMPLETE|0|0|False|SIM1||10076723|No", "error": ""}

To Fetch all the Orders for a specified Portfolio for a specific user:
http://localhost:21000/MultiLegOrderBook?OptionPortfolioName=HTTP_SHORTSTRADDLE_1&User=SIM1

To Fetch all the Orders for a specific user irrespective to the Portfolio:
http://localhost:21000/MultiLegOrderBook?User=SIM1

To Fetch all the Complete Multi Leg Order Book:
http://localhost:21000/MultiLegOrderBook

If you are using it for the first time then it is suggested to use with simulator until you have a better clarity about its functioning.

S.NO
PARAMETER
OPTIONAL/ MANDATORY
DESCRIPTION
REMARKS / EXAMPLE
1
OptionPortfolioName
Mandatory
(String)
Name of the Options Strategy you want to execute.
There are few predefined options strategies whose structure is already defined in the bridge.
However if you want you can create your own options portfolio and that can be used as structure to execute from here.
Any Settings mentioned in the structure like Combined Target etc will be taken care into the execution.
You may wish to do some hit and trial with the simulator for better understanding of how these will work.
A detailed video on our youtube channel will demonstrate the same. 
There are few predefined options strategies which are given below.   

LongCall,
        LongPut,
        ShortCall,
        ShortPut,

        ShortStraddle,
        ShortStrangle,
        LongStraddle,
        LongStrangle,

        BullPutSpread,
        BullCallSpread,
        BearPutSpread,
        BearCallSpread,
        PutDiagonalSpread,
        CallDiagonalSpread,

        IronButterfly,
        IronCondor,
        ReverseIronButterfly,
        ReverseIronCondor,
        CallRatioSpread,
        PutRatioSpread,
        CallBackspread,
        PutBackspread,

        CallCalendarSpread,
        PutCalendarSpread,
        ShortCallCalendar,
        ShortPutCalendar
2
StrategyTag
Mandatory
(String)
Strategy Tag from the Strategies grid. 
In Bridge you can create multiple strategies with their own settings which will be independent of other strategies. Here you can even link strategies to the particular or multiple user(s) / Broker. So that strategy will run only on that selected particular account(s). Read more at bridge help document.
This tag will be used to identify the users and a few other settings to execute the order.
You may refer to options help for more details.
3
Symbol
Mandatory
(String)
This is the Symbol of Future tradable index or stock.
You only need to specify the name, not the full symbol like NIFTY, BANKNIFTY, ACC, HDFC etc.
Even if you have your own custom multi leg structure, still you need to pass Symbol.
This is generally a symbol from your chart.
Bridge will accept valid symbols for Future and Options charts as well.
4
Product
Mandatory
(String)
Product which is used to select the Order Variety Example MIS / NRML. 
Valid values are. MIS, NRML


5
CombinedProfit
Optional
(String)
Combined Profit for the whole portfolio.
This can be passed as Point or percentage Eg 1000 or 20%
This is important to note that you need to pass this as String.
If you don’t want to use this then just pass “0”
6
CombinedLoss
Optional
(String)
Combined Loss for the whole portfolio.
This can be passed as Point or percentage Eg 1000 or 20%
This is important to note that you need to pass this as String.
If you don’t want to use this then just pass “0”
7
LegTarget
Optional
(String)
Target value for the individual leg. This will be set for all the legs.
This can be passed as Point or percentage Eg 100 or 5%
This is important to note that you need to pass this as String.
If you don’t want to use this then just pass “0”
8
LegSL
Optional
(String)
Stop Loss value for the individual leg. This will be set for all the legs.
This can be passed as Point or percentage Eg 100 or 5%
This is important to note that you need to pass this as String.
If you don’t want to use this then just pass “0”
9
Lots
Mandatory
(Number)
Lots for the portfolio.
Specify this even if you have specified Lots in User Defined portfolios. In that case the bridge will ignore this internally.
Should be more than Zero


10
NoDuplicateOrderForSeconds
Optional
(Number)
Here you can specify the seconds for which you do not want to process the duplicate order.
No Duplicate Signal for Seconds
11
EntryPrice
Optional
(Number)
Entry Price if you want entry based on combined premium of the portfolio.


12
SLTOCOST:
Optional
(boolean)
Move SL to Cost
It will be applicable for multi Leg strategies where you set Stop Loss for an individual leg and Leg Tgt SL action is not SqOff all other legs.
For User Defined Portfolios this setting will be ignored by the bridge.
Refer Options Help for more details.
13
PORTLEGS:
Optional
(string)
If you want to specify the legs with the PlaceMultiLegOrder command, then you need to specify this parameter.
Refer to the below table for more details.




LEG DETAILS FOR MULTI LEG CALLS

If you wish, you can send the custom leg details with the signal to Stoxxo. Please follow the below format for a Leg’s details. All details are pipe (|) separated.

Strike:20000|Txn:SELL|Ins:CE|Expiry:11-Jan-2024|Lots:2|Hedge:YES|WT:-5%|Target:Premium:10|SL:Premium:20

In the above details, all parameters are optional except the STRIKE parameter which is mandatory. Character casing doesn’t matter.

You can send the parameters as per requirements, suppose we have leg’s details was already set under the portfolio, now wanted to change its strike and lots only then just send

Strike:20000|Lots:5

Similarly users may want to change strike and expiry only, then send.

Strike:20000|Expiry:W2

How to send Multiple Legs

To send the multiple legs, you can concatenate the leg details with || (double pipes)

For this you can send like this leg1 || leg2 || leg3 etc
Eg Strike:20000|Lots:5||Strike:20000|Expiry:W2

IMPORTANT

Suppose that you're firing a 4 leg portfolio for example an iron condor but you are sending only 2 leg’s details with the function, in this case the bridge will only change the details of the first 2 legs of the portfolio, the remaining 2 legs will remain the same.

Now suppose you're firing a 2 leg portfolio for example a short straddle but you are sending 3 leg’s details with the function, in this case the bridge will first change the details of the first 2 legs of the portfolio and then add the third leg into the portfolio.


Parameter
Possible Values
Remarks
Strike
20000, 
ATM+5, 
ATM-10 etc
Mandatory
ATM+5 means 5 Step away from the ATM.

Suppose ATM is 20000 and Step is 50 for NIFTY then it will select 20250 Strike. 
Txn
BUY / SELL
Mandatory for a new leg
Ins
CE / PE / FUT
Mandatory for a new leg
Expiry
11-Jan-2024, 
11-01-2024,
11/01/2024,
CW (Current Week)
W1 (Next Week)
W2 (Next to Next Week)
CM (Current Month) etc
Mandatory for a new leg

Here we support dynamic expiries so that users need not have an exact expiry date.
Lots
Numeric more than 0
Mandatory for a new leg
Hedge
Yes / No / True / False
Optional
WT
Positive / Negative numbers, percentage are also accepted

5, -10, 2% etc.
Wait and Trade (Optional)
Target
You have to specify the Target Type and then its value

Eg Premium:10
Target Types
Premium,
Underlying,
Strike,
AbsolutePremium,
Delta,
Theta


SL
You have to specify the SL Type and then its value

Eg Premium:20
SL Types
Premium,
Underlying,
Strike,
AbsolutePremium,
Delta,
Theta


