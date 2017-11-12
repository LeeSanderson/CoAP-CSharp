﻿/*
 * Copyright (c) 2011-2015, Longxiang He <helongxiang@smeshlink.com>,
 * SmeshLink Technology Co.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY.
 * 
 * This file is part of the CoAP.NET, a CoAP framework in C#.
 * Please see README for more information.
 */

using System;
using System.Timers;
using Com.AugustCellars.CoAP.Log;
using Com.AugustCellars.CoAP.Net;

namespace Com.AugustCellars.CoAP.Stack
{
    public class BlockwiseLayer : AbstractLayer
    {
        static readonly ILogger log = LogManager.GetLogger(typeof(BlockwiseLayer));

        private Int32 _maxMessageSize;
        private Int32 _defaultBlockSize;
        private Int32 _blockTimeout;

        /// <summary>
        /// Constructs a new blockwise layer.
        /// </summary>
        public BlockwiseLayer(ICoapConfig config)
        {
            _maxMessageSize = config.MaxMessageSize;
            _defaultBlockSize = config.DefaultBlockSize;
            _blockTimeout = config.BlockwiseStatusLifetime;
            log.Debug(m => m("BlockwiseLayer uses MaxMessageSize: {0} and DefaultBlockSize: {1}", _maxMessageSize, _defaultBlockSize));

            config.PropertyChanged += ConfigChanged;
        }

        void ConfigChanged(Object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            ICoapConfig config = (ICoapConfig)sender;
            if (String.Equals(e.PropertyName, "MaxMessageSize")) {
                _maxMessageSize = config.MaxMessageSize;
            }
            else if (String.Equals(e.PropertyName, "DefaultBlockSize")) {
                _defaultBlockSize = config.DefaultBlockSize;
            }
            else if (String.Equals(e.PropertyName, "BlockwiseStatusLifetime")) {
                _blockTimeout = config.BlockwiseStatusLifetime;
            }
        }

        /// <inheritdoc/>
        public override void SendRequest(INextLayer nextLayer, Exchange exchange, Request request)
        {
            if (request.HasOption(OptionType.Block2) && request.Block2.NUM > 0) {
                // This is the case if the user has explicitly added a block option
                // for random access.
                // Note: We do not regard it as random access when the block num is
                // 0. This is because the user might just want to do early block
                // size negotiation but actually wants to receive all blocks.

                log.Debug("Request carries explicit defined block2 option: create random access blockwise status");

                BlockwiseStatus status = new BlockwiseStatus(request.ContentFormat);
                BlockOption block2 = request.Block2;
                status.CurrentSZX = block2.SZX;
                status.CurrentNUM = block2.NUM;
                status.IsRandomAccess = true;
                exchange.ResponseBlockStatus = status;
                base.SendRequest(nextLayer, exchange, request);
            }
            else if (RequiresBlockwise(request)) {
                // This must be a large POST or PUT request
                log.Debug(m => m("Request payload {0}/{1} requires Blockwise.", request.PayloadSize, _maxMessageSize));

                BlockwiseStatus status = FindRequestBlockStatus(exchange, request);
                Request block = GetNextRequestBlock(request, status);
                exchange.RequestBlockStatus = status;
                exchange.CurrentRequest = block;
                base.SendRequest(nextLayer, exchange, block);
            }
            else {
                exchange.CurrentRequest = request;
                base.SendRequest(nextLayer, exchange, request);
            }
        }

        /// <inheritdoc/>
        public override void ReceiveRequest(INextLayer nextLayer, Exchange exchange, Request request)
        {
            if (request.HasOption(OptionType.Block1)) {
                // This must be a large POST or PUT request
                BlockOption block1 = request.Block1;
                    log.Debug(m => m("Request contains block1 option {0}", block1));

                BlockwiseStatus status = FindRequestBlockStatus(exchange, request);
                if (block1.NUM == 0 && status.CurrentNUM > 0) {
                    // reset the blockwise transfer
                        log.Debug("Block1 num is 0, the client has restarted the blockwise transfer. Reset status.");
                    status = new BlockwiseStatus(request.ContentType);
                    exchange.RequestBlockStatus = status;
                }

                if (block1.NUM == status.CurrentNUM) {
                    if (request.ContentType == status.ContentFormat) {
                        status.AddBlock(request.Payload);
                    }
                    else {
                        Response error = Response.CreateResponse(request, StatusCode.RequestEntityIncomplete);
                        error.AddOption(new BlockOption(OptionType.Block1, block1.NUM, block1.SZX, block1.M));
                        error.SetPayload("Changed Content-Format");

                        exchange.CurrentResponse = error;
                        base.SendResponse(nextLayer, exchange, error);
                        return;
                    }

                    status.CurrentNUM = status.CurrentNUM + 1;
                    if (block1.M) {
                            log.Debug("There are more blocks to come. Acknowledge this block.");

                        Response piggybacked = Response.CreateResponse(request, StatusCode.Continue);
                        piggybacked.AddOption(new BlockOption(OptionType.Block1, block1.NUM, block1.SZX, true));
                        piggybacked.Last = false;

                        exchange.CurrentResponse = piggybacked;
                        base.SendResponse(nextLayer, exchange, piggybacked);

                        // do not assemble and deliver the request yet
                    }
                    else {
                            log.Debug("This was the last block. Deliver request");

                        // Remember block to acknowledge. TODO: We might make this a boolean flag in status.
                        exchange.Block1ToAck = block1;

                        // Block2 early negotiation
                        EarlyBlock2Negotiation(exchange, request);

                        // Assemble and deliver
                        Request assembled = new Request(request.Method);
                        AssembleMessage(status, assembled, request);

                        exchange.Request = assembled;
                        base.ReceiveRequest(nextLayer, exchange, assembled);
                    }
                }
                else {
                    // ERROR, wrong number, Incomplete
                        log.Warn(m => m("Wrong block number. Expected {0} but received {1}. Respond with 4.08 (Request Entity Incomplete).", status.CurrentNUM, block1.NUM));
                    Response error = Response.CreateResponse(request, StatusCode.RequestEntityIncomplete);
                    error.AddOption(new BlockOption(OptionType.Block1, block1.NUM, block1.SZX, block1.M));
                    error.SetPayload("Wrong block number");
                    exchange.CurrentResponse = error;
                    base.SendResponse(nextLayer, exchange, error);
                }
            }
            else if (exchange.Response != null && request.HasOption(OptionType.Block2)) {
                // The response has already been generated and the client just wants
                // the next block of it
                BlockOption block2 = request.Block2;
                Response response = exchange.Response;
                BlockwiseStatus status = FindResponseBlockStatus(exchange, response);
                status.CurrentNUM = block2.NUM;
                status.CurrentSZX = block2.SZX;

                Response block = GetNextResponseBlock(response, status);
                block.Token = request.Token;
                block.RemoveOptions(OptionType.Observe);

                if (status.Complete) {
                    // clean up blockwise status
                        log.Debug(m => m("Ongoing is complete {0}", status));
                    exchange.ResponseBlockStatus = null;
                    ClearBlockCleanup(exchange);
                }
                else {
                        log.Debug(m => m("Ongoing is continuing {0}", status));
                }

                exchange.CurrentResponse = block;
                base.SendResponse(nextLayer, exchange, block);

            }
            else {
                EarlyBlock2Negotiation(exchange, request);

                exchange.Request = request;
                base.ReceiveRequest(nextLayer, exchange, request);
            }
        }

        /// <inheritdoc/>
        public override void SendResponse(INextLayer nextLayer, Exchange exchange, Response response)
        {
            BlockOption block1 = exchange.Block1ToAck;
            if (block1 != null) {
                exchange.Block1ToAck = null;
            }

            if (RequiresBlockwise(exchange, response)) {
                log.Debug(m => m("Response payload {0}/{1} requires Blockwise", response.PayloadSize, _maxMessageSize));

                BlockwiseStatus status = FindResponseBlockStatus(exchange, response);

                Response block = GetNextResponseBlock(response, status);

                if (block1 != null) {
                    // in case we still have to ack the last block1
                    block.SetOption(block1);
                }

                if (block.Token == null) {
                    block.Token = exchange.Request.Token;
                }

                if (status.Complete) {
                    // clean up blockwise status
                    log.Debug(m => m("Ongoing finished on first block {0}", status));
                    exchange.ResponseBlockStatus = null;
                    ClearBlockCleanup(exchange);
                }
                else {
                    log.Debug(m => m("Ongoing started {0}", status));
                }

                exchange.CurrentResponse = block;
                base.SendResponse(nextLayer, exchange, block);
            }
            else {
                if (block1 != null) {
                    response.SetOption(block1);
                }

                exchange.CurrentResponse = response;
                // Block1 transfer completed
                ClearBlockCleanup(exchange);
                base.SendResponse(nextLayer, exchange, response);
            }
        }

        /// <inheritdoc/>
        public override void ReceiveResponse(INextLayer nextLayer, Exchange exchange, Response response)
        {
            // do not continue fetching blocks if canceled
            if (exchange.Request.IsCancelled)
            {
                // reject (in particular for Block+Observe)
                if (response.Type != MessageType.ACK)
                {
                    if (log.IsDebugEnabled)
                        log.Debug("Rejecting blockwise transfer for canceled Exchange");
                    EmptyMessage rst = EmptyMessage.NewRST(response);
                    SendEmptyMessage(nextLayer, exchange, rst);
                    // Matcher sets exchange as complete when RST is sent
                }
                return;
            }

            if (!response.HasOption(OptionType.Block1) && !response.HasOption(OptionType.Block2))
            {
                // There is no block1 or block2 option, therefore it is a normal response
                exchange.Response = response;
                base.ReceiveResponse(nextLayer, exchange, response);
                return;
            }

            BlockOption block1 = response.Block1;
            if (block1 != null)
            {
                // TODO: What if request has not been sent blockwise (server error)
                if (log.IsDebugEnabled)
                    log.Debug("Response acknowledges block " + block1);

                BlockwiseStatus status = exchange.RequestBlockStatus;
                if (!status.Complete)
                {
                    // TODO: the response code should be CONTINUE. Otherwise deliver
                    // Send next block
                    Int32 currentSize = 1 << (4 + status.CurrentSZX);
                    Int32 nextNum = status.CurrentNUM + currentSize / block1.Size;
                    if (log.IsDebugEnabled)
                        log.Debug("Send next block num = " + nextNum);
                    status.CurrentNUM = nextNum;
                    status.CurrentSZX = block1.SZX;
                    Request nextBlock = GetNextRequestBlock(exchange.Request, status);
                    if (nextBlock.Token == null)
                        nextBlock.Token = response.Token; // reuse same token
                    exchange.CurrentRequest = nextBlock;
                    base.SendRequest(nextLayer, exchange, nextBlock);
                    // do not deliver response
                }
                else if (!response.HasOption(OptionType.Block2))
                {
                    // All request block have been acknowledged and we receive a piggy-backed
                    // response that needs no blockwise transfer. Thus, deliver it.
                    base.ReceiveResponse(nextLayer, exchange, response);
                }
                else
                {
                    if (log.IsDebugEnabled)
                        log.Debug("Response has Block2 option and is therefore sent blockwise");
                }
            }

            BlockOption block2 = response.Block2;
            if (block2 != null)
            {
                BlockwiseStatus status = FindResponseBlockStatus(exchange, response);

                if (block2.NUM == status.CurrentNUM)
                {
                    // We got the block we expected :-)
                    status.AddBlock(response.Payload);
                    Int32? obs = response.Observe;
                    if (obs.HasValue)
                        status.Observe = obs.Value;

                    // notify blocking progress
                    exchange.Request.FireResponding(response);

                    if (status.IsRandomAccess)
                    {
                        // The client has requested this specifc block and we deliver it
                        exchange.Response = response;
                        base.ReceiveResponse(nextLayer, exchange, response);
                    }
                    else if (block2.M)
                    {
                        if (log.IsDebugEnabled)
                            log.Debug("Request the next response block");

                        Request request = exchange.Request;
                        Int32 num = block2.NUM + 1;
                        Int32 szx = block2.SZX;
                        Boolean m = false;

                        Request block = new Request(request.Method);
                        // NON could make sense over SMS or similar transports
                        block.Type = request.Type;
                        block.Destination = request.Destination;
                        block.SetOptions(request.GetOptions());
                        block.SetOption(new BlockOption(OptionType.Block2, num, szx, m));
                        block.Session = request.Session;
                        // we use the same token to ease traceability (GET without Observe no longer cancels relations)
                        block.Token = response.Token;
                        // make sure not to use Observe for block retrieval
                        block.RemoveOptions(OptionType.Observe);

                        status.CurrentNUM = num;

                        exchange.CurrentRequest = block;
                        base.SendRequest(nextLayer, exchange, block);
                    }
                    else
                    {
                        if (log.IsDebugEnabled)
                            log.Debug("We have received all " + status.BlockCount + " blocks of the response. Assemble and deliver.");
                        Response assembled = new Response(response.StatusCode);
                        AssembleMessage(status, assembled, response);
                        assembled.Type = response.Type;

                        // set overall transfer RTT
                        assembled.RTT = (DateTime.Now - exchange.Timestamp).TotalMilliseconds;

                        // Check if this response is a notification
                        Int32 observe = status.Observe;
                        if (observe != BlockwiseStatus.NoObserve)
                        {
                            assembled.AddOption(Option.Create(OptionType.Observe, observe));
                            // This is necessary for notifications that are sent blockwise:
                            // Reset block number AND container with all blocks
                            exchange.ResponseBlockStatus = null;
                        }

                        if (log.IsDebugEnabled)
                            log.Debug("Assembled response: " + assembled);
                        exchange.Response = assembled;
                        base.ReceiveResponse(nextLayer, exchange, assembled);
                    }

                }
                else
                {
                    // ERROR, wrong block number (server error)
                    // TODO: This scenario is not specified in the draft.
                    // Currently, we reject it and cancel the request.
                    if (log.IsWarnEnabled)
                        log.Warn("Wrong block number. Expected " + status.CurrentNUM + " but received " + block2.NUM + ". Reject response; exchange has failed.");
                    if (response.Type == MessageType.CON)
                    {
                        EmptyMessage rst = EmptyMessage.NewRST(response);
                        base.SendEmptyMessage(nextLayer, exchange, rst);
                    }
                    exchange.Request.IsCancelled = true;
                }
            }
        }

        private void EarlyBlock2Negotiation(Exchange exchange, Request request)
        {
            // Call this method when a request has completely arrived (might have
            // been sent in one piece without blockwise).
            if (request.HasOption(OptionType.Block2))
            {
                BlockOption block2 = request.Block2;
                BlockwiseStatus status2 = new BlockwiseStatus(request.ContentType, block2.NUM, block2.SZX);
                if (log.IsDebugEnabled)
                    log.Debug("Request with early block negotiation " + block2 + ". Create and set new Block2 status: " + status2);
                exchange.ResponseBlockStatus = status2;
            }
        }

        /// <summary>
        /// Notice:
        /// This method is used by SendRequest and ReceiveRequest.
        /// Be careful, making changes to the status in here.
        /// </summary>
        private BlockwiseStatus FindRequestBlockStatus(Exchange exchange, Request request)
        {
            BlockwiseStatus status = exchange.RequestBlockStatus;
            if (status == null) {
                status = new BlockwiseStatus(request.ContentType) {
                    CurrentSZX = BlockOption.EncodeSZX(_defaultBlockSize)
                };
                exchange.RequestBlockStatus = status;
                log.Debug(m => m("There is no assembler status yet. Create and set new Block1 status: {0}", status));
            }
            else {
                log.Debug(m => m("Current Block1 status: {0}", status));
            }
            // sets a timeout to complete exchange
            PrepareBlockCleanup(exchange);
            return status;
        }

        /// <summary>
        /// Notice:
        /// This method is used by SendResponse and ReceiveResponse.
        /// Be careful, making changes to the status in here.
        /// </summary>
        private BlockwiseStatus FindResponseBlockStatus(Exchange exchange, Response response)
        {
            BlockwiseStatus status = exchange.ResponseBlockStatus;

            if (status == null) {
                status = new BlockwiseStatus(response.ContentType) {
                    CurrentSZX = BlockOption.EncodeSZX(_defaultBlockSize)
                };
                exchange.ResponseBlockStatus = status;

                log.Debug(m=> m("There is no blockwise status yet. Create and set new Block2 status: {0}", status));
            }
            else {
                    log.Debug(m => m("Current Block2 status: {0}", status));
            }

            // sets a timeout to complete exchange
            PrepareBlockCleanup(exchange);

            return status;
        }

        private Request GetNextRequestBlock(Request request, BlockwiseStatus status)
        {
            Int32 num = status.CurrentNUM;
            Int32 szx = status.CurrentSZX;
            Request block = new Request(request.Method);
            block.SetOptions(request.GetOptions());
            block.Destination = request.Destination;
            block.Token = request.Token;
            block.Type = MessageType.CON;
            block.Session = request.Session;

            Int32 currentSize = 1 << (4 + szx);
            Int32 from = num * currentSize;
            Int32 to = Math.Min((num + 1) * currentSize, request.PayloadSize);
            Int32 length = to - from;
            Byte[] blockPayload = new Byte[length];
            Array.Copy(request.Payload, from, blockPayload, 0, length);
            block.Payload = blockPayload;

            Boolean m = to < request.PayloadSize;
            block.AddOption(new BlockOption(OptionType.Block1, num, szx, m));

            status.Complete = !m;
            return block;
        }

        private Response GetNextResponseBlock(Response response, BlockwiseStatus status)
        {
            Response block;
            Int32 szx = status.CurrentSZX;
            Int32 num = status.CurrentNUM;

            if (response.HasOption(OptionType.Observe)) {
                // a blockwise notification transmits the first block only
                block = response;
            }
            else {
                block = new Response(response.StatusCode) {
                    Destination = response.Destination,
                    Token = response.Token,
                    Session =  response.Session
                };
                block.SetOptions(response.GetOptions());
                block.TimedOut += (o, e) => response.IsTimedOut = true;
            }

            Int32 payloadSize = response.PayloadSize;
            Int32 currentSize = 1 << (4 + szx);
            Int32 from = num * currentSize;

            if (payloadSize > 0 && payloadSize > from) {
                Int32 to = Math.Min((num + 1) * currentSize, response.PayloadSize);
                Int32 length = to - from;
                Byte[] blockPayload = new Byte[length];
                Boolean m = to < response.PayloadSize;
                block.SetBlock2(szx, m, num);

                // crop payload -- do after calculation of m in case block==response
                Array.Copy(response.Payload, from, blockPayload, 0, length);
                block.Payload = blockPayload;

                // do not complete notifications
                block.Last = !m && !response.HasOption(OptionType.Observe);

                status.Complete = !m;
            }
            else {
                block.AddOption(new BlockOption(OptionType.Block2, num, szx, false));
                block.Last = true;
                status.Complete = true;
            }

            return block;
        }

        private void AssembleMessage(BlockwiseStatus status, Message message, Message last)
        {
            // The assembled request will contain the options of the last block
            message.ID = last.ID;
            message.Source = last.Source;
            message.Token = last.Token;
            message.Type = last.Type;
            message.SetOptions(last.GetOptions());

            Int32 length = 0;
            foreach (Byte[] block in status.Blocks)
                length += block.Length;

            Byte[] payload = new Byte[length];
            Int32 offset = 0;
            foreach (Byte[] block in status.Blocks)
            {
                Array.Copy(block, 0, payload, offset, block.Length);
                offset += block.Length;
            }

            message.Payload = payload;
        }

        private Boolean RequiresBlockwise(Request request)
        {
            if (request.Method == Method.PUT || request.Method == Method.POST || request.Method == Method.FETCH || request.Method == Method.PATCH || request.Method == Method.iPATCH)
                return request.PayloadSize > _maxMessageSize;
            else
                return false;
        }

        private Boolean RequiresBlockwise(Exchange exchange, Response response)
        {
            return response.PayloadSize > _maxMessageSize
                    || exchange.ResponseBlockStatus != null;
        }

        /// <summary>
        /// Schedules a clean-up task.
        /// If a clean-up task already exists, it will be disposed of.
        /// Use the <see cref="ICoapConfig.BlockwiseStatusLifetime"/> to set the timeout.
        /// </summary>
        protected void PrepareBlockCleanup(Exchange exchange)
        {
            Timer timer = new Timer {
                AutoReset = false,
                Interval = _blockTimeout
            };
            timer.Elapsed += (o, e) => BlockwiseTimeout(exchange);

            Timer old = exchange.Set("BlockCleanupTimer", timer) as Timer;
            if (old != null) {
                try {
                    old.Stop();
                    old.Dispose();
                }
                catch (ObjectDisposedException) {
                    // ignore
                }
            }

            timer.Start();
        }

        /// <summary>
        /// Clears the clean-up task.
        /// </summary>
        protected void ClearBlockCleanup(Exchange exchange)
        {
            Timer timer = exchange.Remove("BlockCleanupTimer") as Timer;
            if (timer != null) {
                try {
                    timer.Stop();
                    timer.Dispose();
                }
                catch (ObjectDisposedException) {
                    // ignore
                }
            }
        }

        /// <summary>
        /// Time to try and do clean up.
        /// </summary>
        /// <param name="exchange">exchange to clean up</param>
        private void BlockwiseTimeout(Exchange exchange)
        {
            if (exchange.Request == null) {
                log.Info(m => m("Block1 transfer timed out: {0}", exchange.CurrentRequest));
            }
            else {
                log.Info(m => m("Block2 transfer timed out: {0}", exchange.Request));
            }
            exchange.Complete = true;
        }
    }
}
