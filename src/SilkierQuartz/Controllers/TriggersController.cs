﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Quartz;
using Quartz.Impl.Matchers;
using Quartz.Plugins.RecentHistory;
using SilkierQuartz.Helpers;
using SilkierQuartz.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace SilkierQuartz.Controllers
{
    [Authorize(Policy = SilkierQuartzAuthenticationOptions.AuthorizationPolicyName)]
    public class TriggersController : PageControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var keys = (await Scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup())).OrderBy(x => x.ToString());
            var list = new List<TriggerListItem>();

            foreach (var key in keys)
            {
                var t = await GetTrigger(key);
                var state = await Scheduler.GetTriggerState(key);
                try
                {
                    var tli = new TriggerListItem()
                    {
                        Type = t.GetTriggerType(),
                        TriggerName = t.Key.Name,
                        TriggerGroup = t.Key.Group,
                        IsPaused = state == TriggerState.Paused,
                        JobKey = t.JobKey.ToString(),
                        JobGroup = t.JobKey.Group,
                        JobName = t.JobKey.Name,
                        ScheduleDescription = t.GetScheduleDescription(Services),
                        History = Histogram.Empty,
                        StartTime = t.StartTimeUtc.UtcDateTime.ToDefaultFormat(),
                        EndTime = t.StartTimeUtc.Year == 9999 ? "" : t.FinalFireTimeUtc?.UtcDateTime.ToDefaultFormat(),
                        LastFireTime = t.GetPreviousFireTimeUtc()?.UtcDateTime.ToDefaultFormat(),
                        NextFireTime = t.GetNextFireTimeUtc()?.UtcDateTime.ToDefaultFormat(),
                        ClrType = t.GetType().Name,
                        Description = t.Description,
                        EnableEdit = EnableEdit
                    };
                    list.Add(tli);
                }
                catch (Exception ex)
                {
                    Debug.Fail(ex.Message);
                }
            }

            ViewBag.Groups = (await Scheduler.GetTriggerGroupNames()).GroupArray();

            list = list.OrderBy(x => x.NextFireTime).ToList();
            string prevKey = null;
            foreach (var item in list)
            {
                if (item.JobKey != prevKey)
                {
                    item.JobHeaderSeparator = true;
                    prevKey = item.JobKey;
                }
            }

            ViewBag.EnableEdit = EnableEdit;

            return View(list);
        }

        [HttpGet]
        public async Task<IActionResult> New()
        {
            var model = await TriggerPropertiesViewModel.Create(Scheduler);
            var jobDataMap = new JobDataMapModel() { Template = JobDataMapItemTemplate };

            model.IsNew = true;

            model.Type = TriggerType.Cron;
            model.Priority = 5;
            ViewBag.EnableEdit = EnableEdit;
            return View("Edit", new TriggerViewModel() { Trigger = model, DataMap = jobDataMap });
        }

        [HttpGet]
        public async Task<IActionResult> Edit(string name, string group, bool clone = false)
        {
            if (!EnsureValidKey(name, group)) return BadRequest();

            var key = new TriggerKey(name, group);
            var trigger = await GetTrigger(key);

            var jobDataMap = new JobDataMapModel() { Template = JobDataMapItemTemplate };

            var model = await TriggerPropertiesViewModel.Create(Scheduler);

            model.IsNew = clone;
            model.IsCopy = clone;
            model.Type = trigger.GetTriggerType();
            model.Job = trigger.JobKey.ToString();
            model.TriggerName = trigger.Key.Name;
            model.TriggerGroup = trigger.Key.Group;
            model.OldTriggerName = trigger.Key.Name;
            model.OldTriggerGroup = trigger.Key.Group;

            if (clone)
                model.TriggerName += " - Copy";

            // don't show start time in the past because rescheduling cause triggering missfire policies
            model.StartTimeUtc = trigger.StartTimeUtc > DateTimeOffset.UtcNow ? trigger.StartTimeUtc.UtcDateTime.ToDefaultFormat() : null;

            model.EndTimeUtc = trigger.EndTimeUtc?.UtcDateTime.ToDefaultFormat();

            model.CalendarName = trigger.CalendarName;
            model.Description = trigger.Description;
            model.Priority = trigger.Priority;

            model.MisfireInstruction = trigger.MisfireInstruction;

            switch (model.Type)
            {
                case TriggerType.Cron:
                    model.Cron = CronTriggerViewModel.FromTrigger((ICronTrigger)trigger);
                    break;
                case TriggerType.Simple:
                    model.Simple = SimpleTriggerViewModel.FromTrigger((ISimpleTrigger)trigger);
                    break;
                case TriggerType.Daily:
                    model.Daily = DailyTriggerViewModel.FromTrigger((IDailyTimeIntervalTrigger)trigger);
                    break;
                case TriggerType.Calendar:
                    model.Calendar = CalendarTriggerViewModel.FromTrigger((ICalendarIntervalTrigger)trigger);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported trigger type: " + trigger.GetType().AssemblyQualifiedName);
            }

            jobDataMap.Items.AddRange(trigger.GetJobDataMapModel(Services));

            ViewBag.EnableEdit = EnableEdit;

            return View("Edit", new TriggerViewModel() { Trigger = model, DataMap = jobDataMap });
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> Save([FromForm] TriggerViewModel model)
        {
            var triggerModel = model.Trigger;
            var jobDataMap = (await Request.GetJobDataMapForm()).GetModel(Services);

            var result = new ValidationResult();

            model.Validate(result.Errors);
            ModelValidator.Validate(jobDataMap, result.Errors);

            if (result.Success)
            {
                var builder = TriggerBuilder.Create()
                    .WithIdentity(new TriggerKey(triggerModel.TriggerName, triggerModel.TriggerGroup))
                    .ForJob(jobKey: triggerModel.Job)
                    .UsingJobData(jobDataMap.GetQuartzJobDataMap())
                    .WithDescription(triggerModel.Description)
                    .WithPriority(triggerModel.PriorityOrDefault);

                builder.StartAt(triggerModel.GetStartTimeUtc() ?? DateTime.UtcNow);
                builder.EndAt(triggerModel.GetEndTimeUtc());

                if (!string.IsNullOrEmpty(triggerModel.CalendarName))
                    builder.ModifiedByCalendar(triggerModel.CalendarName);

                if (triggerModel.Type == TriggerType.Cron)
                    triggerModel.Cron.Apply(builder, triggerModel);
                if (triggerModel.Type == TriggerType.Simple)
                    triggerModel.Simple.Apply(builder, triggerModel);
                if (triggerModel.Type == TriggerType.Daily)
                    triggerModel.Daily.Apply(builder, triggerModel);
                if (triggerModel.Type == TriggerType.Calendar)
                    triggerModel.Calendar.Apply(builder, triggerModel);

                var trigger = builder.Build();

                if (triggerModel.IsNew)
                {
                    await Scheduler.ScheduleJob(trigger);
                }
                else
                {
                    await Scheduler.RescheduleJob(new TriggerKey(triggerModel.OldTriggerName, triggerModel.OldTriggerGroup), trigger);
                }
            }

            return Json(result);
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> Delete([FromBody] KeyModel model)
        {
            if (!EnsureValidKey(model)) return BadRequest();

            var key = model.ToTriggerKey();

            if (!await Scheduler.UnscheduleJob(key))
                throw new InvalidOperationException("Cannot unschedule job " + key);

            return NoContent();
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> Resume([FromBody] KeyModel model)
        {
            if (!EnsureValidKey(model)) return BadRequest();
            await Scheduler.ResumeTrigger(model.ToTriggerKey());
            return NoContent();
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> Pause([FromBody] KeyModel model)
        {
            if (!EnsureValidKey(model)) return BadRequest();
            await Scheduler.PauseTrigger(model.ToTriggerKey());
            return NoContent();
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> PauseJob([FromBody] KeyModel model)
        {
            if (!EnsureValidKey(model)) return BadRequest();
            await Scheduler.PauseJob(model.ToJobKey());
            return NoContent();
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> ResumeJob([FromBody] KeyModel model)
        {
            if (!EnsureValidKey(model)) return BadRequest();
            await Scheduler.ResumeJob(model.ToJobKey());
            return NoContent();
        }

        [HttpPost, JsonErrorResponse]
        public async Task<IActionResult> Cron()
        {
            var cron = (await Request.ReadAsStringAsync())?.Trim();
            if (string.IsNullOrEmpty(cron))
                return Json(new { Description = "", Next = new object[0] });

            var desc = "Invalid format.";

            try
            {
                desc = CronExpressionDescriptor.ExpressionDescriptor.GetDescription(cron, Services.Options?.CronExpressionOptions);
            }
            catch
            { }

            var nextDates = new List<string>();

            try
            {
                var qce = new CronExpression(cron);
                var dt = DateTime.Now;
                for (var i = 0; i < 10; i++)
                {
                    var next = qce.GetNextValidTimeAfter(dt);
                    if (next == null)
                        break;
                    nextDates.Add(next.Value.LocalDateTime.ToDefaultFormat());
                    dt = next.Value.LocalDateTime;
                }
            }
            catch
            { }

            return Json(new { Description = desc, Next = nextDates });
        }

        private async Task<ITrigger> GetTrigger(TriggerKey key)
        {
            var trigger = await Scheduler.GetTrigger(key);

            if (trigger == null)
                throw new InvalidOperationException("Trigger " + key + " not found.");

            return trigger;
        }

        [HttpGet, JsonErrorResponse]
        public async Task<IActionResult> AdditionalData()
        {
            var keys = await Scheduler.GetTriggerKeys(GroupMatcher<TriggerKey>.AnyGroup());
            var ehs = Scheduler.Context.GetExecutionHistoryStore();
            var history = ehs != null ? await ehs.FilterLastOfEveryTrigger(10) : null;
            var historyByTrigger = history?.ToLookup(x => x.Trigger);
            var list = new List<object>();
            foreach (var key in keys)
            {
                list.Add(new
                {
                    TriggerName = key.Name,
                    TriggerGroup = key.Group,
                    History = historyByTrigger?.TryGet(key.ToString())?.ToHistogram(),
                });
            }
            return View(list);
        }


        [HttpGet]
        public Task<IActionResult> Duplicate(string name, string group)
        {
            return Edit(name, group, clone: true);
        }

        bool EnsureValidKey(string name, string group) => !(string.IsNullOrEmpty(name) || string.IsNullOrEmpty(group));

        bool EnsureValidKey(KeyModel model) => EnsureValidKey(model.Name, model.Group);
    }
}
