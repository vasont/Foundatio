﻿using System;
using System.Threading.Tasks;
using Foundatio.Extensions;
using Foundatio.Logging;
using Microsoft.Extensions.Logging;
using Foundatio.Messaging;
using Foundatio.Queues;
using Foundatio.Serializer;

namespace Foundatio.Jobs {
    public class WorkItemJob : QueueProcessorJobBase<WorkItemData> {
        protected readonly IMessageBus _messageBus;
        protected readonly WorkItemHandlers _handlers;

        public WorkItemJob(IQueue<WorkItemData> queue, IMessageBus messageBus, WorkItemHandlers handlers, ILoggerFactory loggerFactory = null) : base(queue, loggerFactory) {
            _messageBus = messageBus;
            _handlers = handlers;
            AutoComplete = true;
            EnableLogging = false;
        }
        
        protected async override Task<JobResult> ProcessQueueEntryAsync(JobQueueEntryContext<WorkItemData> context) {
            var workItemDataType = Type.GetType(context.QueueEntry.Value.Type);
            if (workItemDataType == null)
                return JobResult.FailedWithMessage("Could not resolve work item data type.");

            object workItemData;
            try {
                workItemData = await _queue.Serializer.DeserializeAsync(context.QueueEntry.Value.Data, workItemDataType).AnyContext();
            } catch (Exception ex) {
                return JobResult.FromException(ex, "Failed to parse work item data.");
            }

            var handler = _handlers.GetHandler(workItemDataType);
            if (handler == null)
                return JobResult.FailedWithMessage($"Handler for type {workItemDataType.Name} not registered.");

            if (context.QueueEntry.Value.SendProgressReports)
                await _messageBus.PublishAsync(new WorkItemStatus {
                    WorkItemId = context.QueueEntry.Value.WorkItemId,
                    Progress = 0,
                    Type = context.QueueEntry.Value.Type
                }).AnyContext();

            using (var lockValue = await handler.GetWorkItemLockAsync(workItemData, context.CancellationToken).AnyContext()) {
                if (lockValue == null)
                    return JobResult.SuccessWithMessage("Unable to acquire work item lock.");

                var progressCallback = new Func<int, string, Task>(async (progress, message) => {
                    if (handler.AutoRenewLockOnProgress && lockValue != null)
                        await lockValue.RenewAsync().AnyContext();

                    await _messageBus.PublishAsync(new WorkItemStatus {
                        WorkItemId = context.QueueEntry.Value.WorkItemId,
                        Progress = progress,
                        Message = message,
                        Type = context.QueueEntry.Value.Type
                    }).AnyContext();
                });

                try {
                    if (handler.EnableLogging)
                        _logger.Info().Message("Processing {0} work item queue entry ({1}).", workItemDataType.Name, context.QueueEntry.Id).Write();

                    await handler.HandleItemAsync(new WorkItemContext(context, workItemData, JobId, lockValue, progressCallback)).AnyContext();
                } catch (Exception ex) {
                    await context.QueueEntry.AbandonAsync().AnyContext();
                    if (handler.EnableLogging)
                        _logger.Error().Message("Error processing {0} work item queue entry ({1}).", workItemDataType.Name, context.QueueEntry.Id).Write();

                    return JobResult.FromException(ex, $"Error in handler {workItemDataType.Name}.");
                }

                await context.QueueEntry.CompleteAsync().AnyContext();
                if (handler.EnableLogging)
                    _logger.Info().Message("Completed {0} work item queue entry ({1}).", workItemDataType.Name, context.QueueEntry.Id).Write();

                if (context.QueueEntry.Value.SendProgressReports)
                    await _messageBus.PublishAsync(new WorkItemStatus {
                        WorkItemId = context.QueueEntry.Value.WorkItemId,
                        Progress = 100,
                        Type = context.QueueEntry.Value.Type
                    }).AnyContext();

                return JobResult.Success;
            }
        }
    }
}
