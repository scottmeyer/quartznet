#region License
/* 
 * All content copyright Terracotta, Inc., unless otherwise indicated. All rights reserved. 
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not 
 * use this file except in compliance with the License. You may obtain a copy 
 * of the License at 
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0 
 *   
 * Unless required by applicable law or agreed to in writing, software 
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT 
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the 
 * License for the specific language governing permissions and limitations 
 * under the License.
 * 
 */
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

using Common.Logging;

using Quartz.Collection;
using Quartz.Impl;
using Quartz.Spi;

namespace Quartz.Simpl
{
	/// <summary>
	/// This class implements a <see cref="IJobStore" /> that
	/// utilizes RAM as its storage device.
	/// <p>
	/// As you should know, the ramification of this is that access is extrememly
	/// fast, but the data is completely volatile - therefore this <see cref="IJobStore" />
	/// should not be used if true persistence between program shutdowns is
	/// required.
	/// </p>
	/// </summary>
	/// <author>James House</author>
	/// <author>Sharada Jambula</author>
	/// <author>Marko Lahma (.NET)</author>
	public class RAMJobStore : IJobStore
	{
        private readonly Dictionary<JobKey, JobWrapper> jobsByKey = new Dictionary<JobKey, JobWrapper>(1000);
        private readonly Dictionary<TriggerKey, TriggerWrapper> triggersByKey = new Dictionary<TriggerKey, TriggerWrapper>(1000);
        private readonly Dictionary<string, IDictionary<JobKey, JobWrapper>> jobsByGroup = new Dictionary<string, IDictionary<JobKey, JobWrapper>>(25);
        private readonly Dictionary<string, IDictionary<TriggerKey, TriggerWrapper>> triggersByGroup = new Dictionary<string, IDictionary<TriggerKey, TriggerWrapper>>(25);
		private readonly TreeSet<TriggerWrapper> timeTriggers = new TreeSet<TriggerWrapper>(new TriggerComparator());
		private readonly Dictionary<string, ICalendar> calendarsByName = new Dictionary<string, ICalendar>(5);
		private readonly List<TriggerWrapper> triggers = new List<TriggerWrapper>(1000);
		private readonly object lockObject = new object();
        private readonly Collection.HashSet<string> pausedTriggerGroups = new Collection.HashSet<string>();
        private readonly Collection.HashSet<string> pausedJobGroups = new Collection.HashSet<string>();
        private readonly Collection.HashSet<JobKey> blockedJobs = new Collection.HashSet<JobKey>();
		private TimeSpan misfireThreshold = TimeSpan.FromSeconds(5);
		private ISchedulerSignaler signaler;
		
		private readonly ILog log;


        /// <summary>
        /// Initializes a new instance of the <see cref="RAMJobStore"/> class.
        /// </summary>
	    public RAMJobStore()
	    {
	        log = LogManager.GetLogger(GetType());
	    }

	    /// <summary> 
		/// The time span by which a trigger must have missed its
		/// next-fire-time, in order for it to be considered "misfired" and thus
		/// have its misfire instruction applied.
		/// </summary>
		[TimeSpanParseRule(TimeSpanParseRule.Milliseconds)]
		public virtual TimeSpan MisfireThreshold
		{
			get { return misfireThreshold; }
			set
			{
				if (value.TotalMilliseconds < 1)
				{
					throw new ArgumentException("Misfirethreashold must be larger than 0");
				}
				misfireThreshold = value;
			}
		}

        private static long ftrCtr = SystemTime.UtcNow().Ticks;

        /// <summary>
	    /// Gets the fired trigger record id.
	    /// </summary>
	    /// <returns>The fired trigger record id.</returns>
	    protected virtual string GetFiredTriggerRecordId()
	    {
	        long value = Interlocked.Increment(ref ftrCtr);
	        return Convert.ToString(value, CultureInfo.InvariantCulture);
	    }

	    /// <summary>
		/// Called by the QuartzScheduler before the <see cref="IJobStore" /> is
		/// used, in order to give the it a chance to Initialize.
		/// </summary>
		public virtual void Initialize(ITypeLoadHelper loadHelper, ISchedulerSignaler s)
		{
			signaler = s;
			Log.Info("RAMJobStore initialized.");
		}

        /// <summary>
        /// Called by the QuartzScheduler to inform the <see cref="IJobStore" /> that
        /// the scheduler has started.
        /// </summary>
		public virtual void SchedulerStarted()
		{
			// nothing to do
		}

		/// <summary>
		/// Called by the QuartzScheduler to inform the <see cref="IJobStore" /> that
		/// it should free up all of it's resources because the scheduler is
		/// shutting down.
		/// </summary>
		public virtual void Shutdown()
		{
		}

		/// <summary>
		/// Returns whether this instance supports persistence.
		/// </summary>
		/// <value></value>
		/// <returns></returns>
	    public virtual bool SupportsPersistence
	    {
	        get { return false; }
	    }


        /// <summary>
        /// Clears (deletes!) all scheduling data - all <see cref="IJob"/>s, <see cref="ITrigger" />s
        /// <see cref="ICalendar"/>s.
        /// </summary>
        public void ClearAllSchedulingData()
        {
            lock (lockObject)
            {
                // unschedule jobs (delete triggers)
                IList<string> lst = GetTriggerGroupNames();
                foreach (string group in lst)
                {
                    IList<TriggerKey> keys = GetTriggerKeys(group);
                    foreach (TriggerKey key in keys)
                    {
                        RemoveTrigger(key);
                    }
                }
                // delete jobs
                lst = GetJobGroupNames();
                foreach (string group in lst)
                {
                    IList<JobKey> keys = GetJobKeys(group);
                    foreach (JobKey key in keys)
                    {
                        RemoveJob(key);
                    }
                }
                // delete calendars
                lst = GetCalendarNames();
                foreach (string name in lst)
                {
                    RemoveCalendar(name);
                }
            }
        }
    

	    protected ILog Log
	    {
	        get { return log; }
	    }

	    /// <summary>
		/// Store the given <see cref="IJobDetail" /> and <see cref="ITrigger" />.
		/// </summary>
		/// <param name="newJob">The <see cref="IJobDetail" /> to be stored.</param>
		/// <param name="newTrigger">The <see cref="ITrigger" /> to be stored.</param>
        public virtual void StoreJobAndTrigger(IJobDetail newJob, IOperableTrigger newTrigger)
		{
			StoreJob(newJob, false);
			StoreTrigger(newTrigger, false);
		}

	    /// <summary>
	    /// Returns true if the given job group is paused.
	    /// </summary>
	    /// <param name="groupName">Job group name</param>
	    /// <returns></returns>
	    public virtual bool IsJobGroupPaused(string groupName)
	    {
            return pausedJobGroups.Contains(groupName);
	    }

	    /// <summary>
	    /// returns true if the given TriggerGroup is paused.
	    /// </summary>
	    /// <param name="groupName"></param>
	    /// <returns></returns>
	    public virtual bool IsTriggerGroupPaused(string groupName)
	    {
	       return pausedTriggerGroups.Contains(groupName);
	    }

	    /// <summary>
		/// Store the given <see cref="IJob" />.
		/// </summary>
		/// <param name="newJob">The <see cref="IJob" /> to be stored.</param>
		/// <param name="replaceExisting">If <see langword="true" />, any <see cref="IJob" /> existing in the
		/// <see cref="IJobStore" /> with the same name and group should be
		/// over-written.</param>
		public virtual void StoreJob(IJobDetail newJob, bool replaceExisting)
		{
            JobWrapper jw = new JobWrapper((IJobDetail)newJob.Clone());

			bool repl = false;

            lock (lockObject) {
                
                if (jobsByKey.ContainsKey(jw.key)) {
                    if (!replaceExisting) {
                        throw new ObjectAlreadyExistsException(newJob);
                    }
                    repl = true;
                }

                if (!repl)
				{
					// get job group
					IDictionary<JobKey, JobWrapper> grpMap;
					if (!jobsByGroup.TryGetValue(newJob.Key.Group, out grpMap))
					{
                        grpMap = new Dictionary<JobKey, JobWrapper>(100);
						jobsByGroup[newJob.Key.Group] = grpMap;
					}
					// add to jobs by group
					grpMap[newJob.Key] = jw;
					// add to jobs by FQN map
					jobsByKey[jw.key] = jw;
				}
				else
				{
					// update job detail
					JobWrapper orig = jobsByKey[jw.key];
                    orig.jobDetail = jw.jobDetail;
				}
			}
		}

		/// <summary>
		/// Remove (delete) the <see cref="IJob" /> with the given
		/// name, and any <see cref="ITrigger" /> s that reference
		/// it.
		/// </summary>
		/// <returns>
		/// 	<see langword="true" /> if a <see cref="IJob" /> with the given name and
		/// group was found and removed from the store.
		/// </returns>
        public virtual bool RemoveJob(JobKey jobKey)
		{
			bool found = false;

            lock (lockObject)
			{
                IList<IOperableTrigger> triggersForJob = GetTriggersForJob(jobKey);
                foreach (IOperableTrigger trigger in triggersForJob)
                {
                    RemoveTrigger(trigger.Key);
                    found = true;
                }
                
                JobWrapper tempObject;
                if (jobsByKey.TryGetValue(jobKey, out tempObject))
				{
                    jobsByKey.Remove(jobKey);
				}
				found = (tempObject != null) | found;
				if (found)
				{
				    IDictionary<JobKey, JobWrapper> grpMap;
				    jobsByGroup.TryGetValue(jobKey.Group, out grpMap);
					if (grpMap != null)
					{
						grpMap.Remove(jobKey);
						if (grpMap.Count == 0)
						{
                            jobsByGroup.Remove(jobKey.Group);
						}
					}
				}
			}

			return found;
		}

	    public bool RemoveJobs(IList<JobKey> jobKeys)
	    {
	        bool allFound = true;

	        lock (lockObject)
	        {
	            foreach (JobKey key in jobKeys)
	            {
	                allFound = RemoveJob(key) && allFound;
	            }
	        }

	        return allFound;
	    }

	    public bool RemoveTriggers(IList<TriggerKey> triggerKeys)
	    {
	        bool allFound = true;

	        lock (lockObject)
	        {
	            foreach (TriggerKey key in triggerKeys)
	            {
	                allFound = RemoveTrigger(key) && allFound;
	            }
	        }

	        return allFound;
	    }

	    public void StoreJobsAndTriggers(IDictionary<IJobDetail, IList<ITrigger>> triggersAndJobs, bool replace)
	    {
	        lock (lockObject)
	        {
	            // make sure there are no collisions...
	            if (!replace)
	            {
	                foreach (IJobDetail job in triggersAndJobs.Keys)
	                {
	                    if (CheckExists(job.Key))
	                    {
	                        throw new ObjectAlreadyExistsException(job);
	                    }
	                    foreach (ITrigger trigger in triggersAndJobs[job])
	                    {
	                        if (CheckExists(trigger.Key))
	                        {
	                            throw new ObjectAlreadyExistsException(trigger);
	                        }
	                    }
	                }
	            }
	            // do bulk add...
	            foreach (IJobDetail job in triggersAndJobs.Keys)
	            {
	                StoreJob(job, true);
	                foreach (ITrigger trigger in triggersAndJobs[job])
	                {
	                    StoreTrigger((IOperableTrigger) trigger, true);
	                }
	            }
	        }
	    }

        /// <summary>
        /// Remove (delete) the <see cref="ITrigger" /> with the
        /// given name.
        /// </summary>
        /// <returns>
        /// 	<see langword="true" /> if a <see cref="ITrigger" /> with the given
        /// name and group was found and removed from the store.
        /// </returns>
        public virtual bool RemoveTrigger(TriggerKey triggerKey)
	    {
	        return RemoveTrigger(triggerKey, true);
	    }

	    /// <summary>
		/// Store the given <see cref="ITrigger" />.
		/// </summary>
		/// <param name="newTrigger">The <see cref="ITrigger" /> to be stored.</param>
		/// <param name="replaceExisting">If <see langword="true" />, any <see cref="ITrigger" /> existing in
		/// the <see cref="IJobStore" /> with the same name and group should
		/// be over-written.</param>
        public virtual void StoreTrigger(IOperableTrigger newTrigger, bool replaceExisting)
		{
            TriggerWrapper tw = new TriggerWrapper((IOperableTrigger) newTrigger.Clone());

            lock (lockObject)
            {

	            TriggerWrapper wrapper;
                if (triggersByKey.TryGetValue(tw.key, out wrapper))
			    {
				    if (!replaceExisting)
				    {
					    throw new ObjectAlreadyExistsException(newTrigger);
				    }

                    // don't delete orphaned job, this trigger has the job anyways
				    RemoveTrigger(newTrigger.Key, false);
			    }

			    if (RetrieveJob(newTrigger.JobKey) == null)
			    {
				    throw new JobPersistenceException("The job (" + newTrigger.JobKey +
				                                      ") referenced by the trigger does not exist.");
			    }

				// add to triggers array
				triggers.Add(tw);

				// add to triggers by group
				IDictionary<TriggerKey, TriggerWrapper> grpMap;
			    triggersByGroup.TryGetValue(newTrigger.Key.Group, out grpMap);

				if (grpMap == null)
				{
					grpMap = new Dictionary<TriggerKey, TriggerWrapper>(100);
                    triggersByGroup[newTrigger.Key.Group] = grpMap;
				}
				grpMap[newTrigger.Key] = tw;
				// add to triggers by FQN map
				triggersByKey[tw.key] = tw;

                if (pausedTriggerGroups.Contains(newTrigger.Key.Group) || pausedJobGroups.Contains(newTrigger.JobKey.Group))
                {
                    tw.state = InternalTriggerState.Paused;
                    if (blockedJobs.Contains(tw.jobKey))
                    {
                        tw.state = InternalTriggerState.PausedAndBlocked;
                    }
                }
                else if (blockedJobs.Contains(tw.jobKey))
                {
                    tw.state = InternalTriggerState.Blocked;
                }
                else
                {
                    timeTriggers.Add(tw);
                }
			}
		}

		/// <summary>
		/// Remove (delete) the <see cref="ITrigger" /> with the
		/// given name.
		/// </summary>
		/// <returns>
		/// 	<see langword="true" /> if a <see cref="ITrigger" /> with the given
		/// name and group was found and removed from the store.
		/// </returns>
		/// <param name="removeOrphanedJob">Whether to delete orpahaned job details from scheduler if job becomes orphaned from removing the trigger.</param>
        public virtual bool RemoveTrigger(TriggerKey key, bool removeOrphanedJob)
		{
		    bool found;
			lock (lockObject)
			{
				// remove from triggers by FQN map
                found = triggersByKey.Remove(key);
                if (found)
                {
                    TriggerWrapper tw = null;
                    // remove from triggers by group
                    IDictionary<TriggerKey, TriggerWrapper> grpMap = triggersByGroup[key.Group];
                    if (grpMap != null)
                    {
                        grpMap.Remove(key);
                        if (grpMap.Count == 0)
                        {
                            triggersByGroup.Remove(key.Group);
                        }
                    }
                    // remove from triggers array
                    for (int i = 0; i < triggers.Count; ++i)
                    {
                        tw = triggers[i];
                        if (key.Equals(tw.key))
                        {
                            triggers.RemoveAt(i);
                            break;
                        }
                    }
                    timeTriggers.Remove(tw);


                    if (removeOrphanedJob)
                    {
                        JobWrapper jw = (JobWrapper)jobsByKey[tw.jobKey];
                        IList<IOperableTrigger> trigs = GetTriggersForJob(tw.jobKey);
                        if ((trigs == null || trigs.Count == 0) && !jw.jobDetail.Durable)
                        {
                            if (RemoveJob(jw.key))
                            {
                                signaler.NotifySchedulerListenersJobDeleted(jw.key);
                            }
                        }
                    }
                }
			}

			return found;
		}


		/// <summary>
		/// Replaces the trigger.
		/// </summary>
		/// <param name="newTrigger">The new trigger.</param>
		/// <returns></returns>
        public virtual bool ReplaceTrigger(TriggerKey triggerKey, IOperableTrigger newTrigger)
		{
			bool found;

			lock (lockObject)
			{
				// remove from triggers by FQN map
                TriggerWrapper tw;
                if (triggersByKey.TryGetValue(triggerKey, out tw))
				{
                    triggersByKey.Remove(triggerKey);
				}
				found = tw != null;

				if (found)
				{
                    if (!tw.trigger.JobKey.Equals(newTrigger.JobKey))
					{
						throw new JobPersistenceException("New trigger is not related to the same job as the old trigger.");
					}

					tw = null;
					// remove from triggers by group
					IDictionary<TriggerKey, TriggerWrapper> grpMap;
				    triggersByGroup.TryGetValue(triggerKey.Group, out grpMap);

					if (grpMap != null)
					{
                        grpMap.Remove(triggerKey);
						if (grpMap.Count == 0)
						{
                            triggersByGroup.Remove(triggerKey.Group);
						}
					}
					// remove from triggers array
					for ( int i = 0; i < triggers.Count; ++i)
					{
						tw = triggers[i];
                        if (triggerKey.Equals(tw.key))
						{
							triggers.RemoveAt(i);
							break;
						}
					}
					timeTriggers.Remove(tw);

					try
					{
						StoreTrigger(newTrigger, false);
					}
					catch (JobPersistenceException)
					{
						StoreTrigger(tw.trigger, false); // put previous trigger back...
						throw;
					}
				}
			}

			return found;
		}

		/// <summary>
		/// Retrieve the <see cref="IJobDetail" /> for the given
		/// <see cref="IJob" />.
		/// </summary>
		/// <returns>
		/// The desired <see cref="IJob" />, or null if there is no match.
		/// </returns>
        public virtual IJobDetail RetrieveJob(JobKey jobKey)
		{
            lock (lockObject)
            {
                JobWrapper jw;
                jobsByKey.TryGetValue(jobKey, out jw);
                return (jw != null) ? (IJobDetail) jw.jobDetail.Clone() : null;
            }
        }

		/// <summary>
		/// Retrieve the given <see cref="ITrigger" />.
		/// </summary>
		/// <returns>
		/// The desired <see cref="ITrigger" />, or null if there is no match.
		/// </returns>
        public virtual IOperableTrigger RetrieveTrigger(TriggerKey triggerKey)
		{
            lock (lockObject)
            {
                TriggerWrapper tw;
                triggersByKey.TryGetValue(triggerKey, out tw);
                return (tw != null) ? (IOperableTrigger)tw.trigger.Clone() : null;
            }
		}

	    /**
         * Determine whether a {@link Job} with the given identifier already 
         * exists within the scheduler.
         * 
         * @param jobKey the identifier to check for
         * @return true if a Job exists with the given identifier
         * @throws SchedulerException 
         */
	    public bool CheckExists(JobKey jobKey)
	    {
	        lock (lockObject)
	        {
	            return jobsByKey.ContainsKey(jobKey);
	        }
	    }

	    /**
         * Determine whether a {@link Trigger} with the given identifier already 
         * exists within the scheduler.
         * 
         * @param triggerKey the identifier to check for
         * @return true if a Trigger exists with the given identifier
         * @throws SchedulerException 
         */
	    public bool CheckExists(TriggerKey triggerKey)
	    {
	        lock (lockObject)
	        {
                return triggersByKey.ContainsKey(triggerKey);
	        }
	    }

		/// <summary>
		/// Get the current state of the identified <see cref="ITrigger" />.
		/// </summary>
        /// <seealso cref="TriggerState.Normal" />
        /// <seealso cref="TriggerState.Paused" />
        /// <seealso cref="TriggerState.Complete" />
        /// <seealso cref="TriggerState.Error" />
        /// <seealso cref="TriggerState.Blocked" />
        /// <seealso cref="TriggerState.None"/>
        public virtual TriggerState GetTriggerState(TriggerKey triggerKey)
		{
            lock (lockObject)
            {
                TriggerWrapper tw;
                triggersByKey.TryGetValue(triggerKey, out tw);

                if (tw == null)
                {
                    return TriggerState.None;
                }
                if (tw.state == InternalTriggerState.Complete)
                {
                    return TriggerState.Complete;
                }
                if (tw.state == InternalTriggerState.Paused)
                {
                    return TriggerState.Paused;
                }
                if (tw.state == InternalTriggerState.PausedAndBlocked)
                {
                    return TriggerState.Paused;
                }
                if (tw.state == InternalTriggerState.Blocked)
                {
                    return TriggerState.Blocked;
                }
                if (tw.state == InternalTriggerState.Error)
                {
                    return TriggerState.Error;
                }
                return TriggerState.Normal;
            }
		}

		/// <summary>
		/// Store the given <see cref="ICalendar" />.
		/// </summary>
		/// <param name="name">The name.</param>
		/// <param name="calendar">The <see cref="ICalendar" /> to be stored.</param>
		/// <param name="replaceExisting">If <see langword="true" />, any <see cref="ICalendar" /> existing
		/// in the <see cref="IJobStore" /> with the same name and group
		/// should be over-written.</param>
		/// <param name="updateTriggers">If <see langword="true" />, any <see cref="ITrigger" />s existing
		/// in the <see cref="IJobStore" /> that reference an existing
		/// Calendar with the same name with have their next fire time
		/// re-computed with the new <see cref="ICalendar" />.</param>
		public virtual void StoreCalendar(string name, ICalendar calendar, bool replaceExisting,
		                                  bool updateTriggers)
		{
            calendar = (ICalendar) calendar.Clone();

            lock (lockObject)
            {
                ICalendar obj;
		        calendarsByName.TryGetValue(name, out obj);

			    if (obj != null && replaceExisting == false)
			    {
				    throw new ObjectAlreadyExistsException(string.Format(CultureInfo.InvariantCulture, "Calendar with name '{0}' already exists.", name));
			    }
		        if (obj != null)
		        {
		            calendarsByName.Remove(name);
		        }

		        calendarsByName[name] = calendar;

			    if (obj != null && updateTriggers)
			    {
					List<TriggerWrapper> trigs = GetTriggerWrappersForCalendar(name);
					for (int i = 0; i < trigs.Count; ++i)
					{
						TriggerWrapper tw = trigs[i];
						IOperableTrigger trig = tw.trigger;
                        bool removed = timeTriggers.Remove(tw);

						trig.UpdateWithNewCalendar(calendar, MisfireThreshold);

						if (removed)
						{
							timeTriggers.Add(tw);
						}
					}
				}
			}
		}

		/// <summary>
		/// Remove (delete) the <see cref="ICalendar" /> with the
		/// given name.
		/// <p>
		/// If removal of the <see cref="ICalendar" /> would result in
		/// <see cref="ITrigger" />s pointing to non-existent calendars, then a
		/// <see cref="JobPersistenceException" /> will be thrown.</p>
		/// </summary>
		/// <param name="calName">The name of the <see cref="ICalendar" /> to be removed.</param>
		/// <returns>
		/// 	<see langword="true" /> if a <see cref="ICalendar" /> with the given name
		/// was found and removed from the store.
		/// </returns>
		public virtual bool RemoveCalendar(string calName)
		{
			int numRefs = 0;

			lock (lockObject)
			{
				foreach (TriggerWrapper triggerWrapper in triggers)
				{
                    IOperableTrigger trigg = triggerWrapper.trigger;
					if (trigg.CalendarName != null && trigg.CalendarName.Equals(calName))
					{
						numRefs++;
					}
				}
			}

			if (numRefs > 0)
			{
				throw new JobPersistenceException("Calender cannot be removed if it referenced by a Trigger!");
			}

			return calendarsByName.Remove(calName);
		}

		/// <summary>
		/// Retrieve the given <see cref="ITrigger" />.
		/// </summary>
		/// <param name="calName">The name of the <see cref="ICalendar" /> to be retrieved.</param>
		/// <returns>
		/// The desired <see cref="ICalendar" />, or null if there is no match.
		/// </returns>
		public virtual ICalendar RetrieveCalendar(string calName)
		{
            lock (lockObject)
            {
                ICalendar calendar;
                calendarsByName.TryGetValue(calName, out calendar);
                if (calendar != null)
                {
                    return (ICalendar) calendar.Clone();
                }
                return null;
            }
		}

	    /// <summary>
		/// Get the number of <see cref="IJobDetail" /> s that are
		/// stored in the <see cref="IJobStore" />.
		/// </summary>
		public virtual int GetNumberOfJobs()
		{
            lock (lockObject)
            {
                return jobsByKey.Count;
            }
		}

		/// <summary>
		/// Get the number of <see cref="ITrigger" /> s that are
		/// stored in the <see cref="IJobStore" />.
		/// </summary>
		public virtual int GetNumberOfTriggers()
		{
            lock (lockObject)
            {
                return triggers.Count;
            }
		}

		/// <summary>
		/// Get the number of <see cref="ICalendar" /> s that are
		/// stored in the <see cref="IJobStore" />.
		/// </summary>
		public virtual int GetNumberOfCalendars()
		{
            lock (lockObject)
            {
                return calendarsByName.Count;
            }
		}

		/// <summary>
		/// Get the names of all of the <see cref="IJob" /> s that
		/// have the given group name.
		/// </summary>
		public virtual IList<JobKey> GetJobKeys(string groupName)
		{
            List<JobKey> outList;
			lock (lockObject)
			{
                IDictionary<JobKey, JobWrapper> grpMap = jobsByGroup[groupName];
			    if (grpMap != null)
			    {
				    outList = new List<JobKey>(grpMap.Count);
				    foreach (KeyValuePair<JobKey, JobWrapper> pair in grpMap)
				    {
						if (pair.Value != null)
						{
							outList.Add(pair.Value.jobDetail.Key);
						}
					}
				}
                else
                {
                    outList = new List<JobKey>(0);
                }
			}

			return outList;
		}

		/// <summary>
		/// Get the names of all of the <see cref="ICalendar" /> s
		/// in the <see cref="IJobStore" />.
		/// <p>
		/// If there are no ICalendars in the given group name, the result should be
		/// a zero-length array (not <see langword="null" />).
		/// </p>
		/// </summary>
		public virtual IList<string> GetCalendarNames()
		{
            lock (lockObject)
            {
                return new List<string>(calendarsByName.Keys).ToArray();
            }
		}

		/// <summary>
		/// Get the names of all of the <see cref="ITrigger" /> s
		/// that have the given group name.
		/// </summary>
        public virtual IList<TriggerKey> GetTriggerKeys(string groupName)
		{
            List<TriggerKey> outList;
            lock (lockObject)
	        {
                IDictionary<TriggerKey, TriggerWrapper> grpMap;
	            triggersByGroup.TryGetValue(groupName, out grpMap);

                if (grpMap != null)
			    {
					    outList = new List<TriggerKey>(grpMap.Count);
				        foreach (KeyValuePair<TriggerKey, TriggerWrapper> pair in grpMap)
				        {
						    if (pair.Value != null)
						    {
							    outList.Add(pair.Value.trigger.Key);
						    }
					    }
				    }
			    else
			    {
				    outList = new List<TriggerKey>(0);
			    }
            }

			return outList;
		}

		/// <summary>
		/// Get the names of all of the <see cref="IJob" />
		/// groups.
		/// </summary>
		public virtual IList<string> GetJobGroupNames()
		{
            lock (lockObject)
			{
			    return  new List<string>(jobsByGroup.Keys).ToArray();
			}
		}

		/// <summary>
		/// Get the names of all of the <see cref="ITrigger" /> groups.
		/// </summary>
		public virtual IList<string> GetTriggerGroupNames()
		{
            lock (lockObject)
            {
                return new List<string>(triggersByGroup.Keys).ToArray();
            }
		}

		/// <summary>
		/// Get all of the Triggers that are associated to the given Job.
		/// <p>
		/// If there are no matches, a zero-length array should be returned.
		/// </p>
		/// </summary>
        public virtual IList<IOperableTrigger> GetTriggersForJob(JobKey jobKey)
		{
			var trigList = new List<IOperableTrigger>();

			lock (lockObject)
			{
				for (int i = 0; i < triggers.Count; i++)
				{
					TriggerWrapper tw = triggers[i];
					if (tw.jobKey.Equals(jobKey))
					{
                        trigList.Add((IOperableTrigger)tw.trigger.Clone());
					}
				}
			}

			return trigList;
		}

		/// <summary>
		/// Gets the trigger wrappers for job.
		/// </summary>
		/// <returns></returns>
        protected virtual List<TriggerWrapper> GetTriggerWrappersForJob(JobKey jobKey)
		{
			var trigList = new List<TriggerWrapper>();

			lock (lockObject)
			{
				for (int i = 0; i < triggers.Count; i++)
				{
					TriggerWrapper tw = triggers[i];
					if (tw.jobKey.Equals(jobKey))
					{
						trigList.Add(tw);
					}
				}
			}

			return trigList;
		}

		/// <summary>
		/// Gets the trigger wrappers for calendar.
		/// </summary>
		/// <param name="calName">Name of the cal.</param>
		/// <returns></returns>
		protected virtual List<TriggerWrapper> GetTriggerWrappersForCalendar(string calName)
		{
			var trigList = new List<TriggerWrapper>();

			lock (lockObject)
			{
				for (int i = 0; i < triggers.Count; i++)
				{
					TriggerWrapper tw = triggers[i];
					string tcalName = tw.trigger.CalendarName;
					if (tcalName != null && tcalName.Equals(calName))
					{
						trigList.Add(tw);
					}
				}
			}

			return trigList;
		}

		/// <summary> 
		/// Pause the <see cref="ITrigger" /> with the given name.
		/// </summary>
        public virtual void PauseTrigger(TriggerKey triggerKey)
		{
            lock (lockObject)
            {
                TriggerWrapper tw = triggersByKey[triggerKey];

			    // does the trigger exist?
			    if (tw == null || tw.trigger == null)
			    {
				    return;
			    }
			    // if the trigger is "complete" pausing it does not make sense...
                if (tw.state == InternalTriggerState.Complete)
			    {
				    return;
			    }

                if (tw.state == InternalTriggerState.Blocked)
				{
                    tw.state = InternalTriggerState.PausedAndBlocked;
				}
				else
				{
                    tw.state = InternalTriggerState.Paused;
				}
				timeTriggers.Remove(tw);
			}
		}

		/// <summary>
		/// Pause all of the <see cref="ITrigger" />s in the given group.
		/// <p>
		/// The JobStore should "remember" that the group is paused, and impose the
		/// pause on any new triggers that are added to the group while the group is
		/// paused.
		/// </p>
		/// </summary>
		public virtual void PauseTriggerGroup(string groupName)
		{
            lock (lockObject)
			{
				if (pausedTriggerGroups.Contains(groupName))
				{
					return;
				}
				pausedTriggerGroups.Add(groupName);
				IList<TriggerKey> keys = GetTriggerKeys(groupName);

				foreach (TriggerKey key in keys)
				{
				    PauseTrigger(key);
				}
			}
		}

		/// <summary> 
		/// Pause the <see cref="IJobDetail" /> with the given
		/// name - by pausing all of its current <see cref="ITrigger" />s.
		/// </summary>
        public virtual void PauseJob(JobKey jobKey)
		{
            lock (lockObject)
            {
                IList<IOperableTrigger> triggersForJob = GetTriggersForJob(jobKey);
                foreach (IOperableTrigger trigger in triggersForJob)
                {
                    PauseTrigger(trigger.Key);
                }
            }
		}

		/// <summary>
		/// Pause all of the <see cref="IJobDetail" />s in the
		/// given group - by pausing all of their <see cref="ITrigger" />s.
		/// <p>
		/// The JobStore should "remember" that the group is paused, and impose the
		/// pause on any new jobs that are added to the group while the group is
		/// paused.
		/// </p>
		/// </summary>
		public virtual void PauseJobGroup(string groupName)
		{
            lock (lockObject)
			{
                if (!pausedJobGroups.Contains(groupName))
                {
                    pausedJobGroups.Add(groupName);
                }
				IList<JobKey> keys = GetJobKeys(groupName);

				foreach (JobKey key in keys)
				{
				    IList<IOperableTrigger> triggersForJob = GetTriggersForJob(key);
                    foreach (IOperableTrigger trigger in triggersForJob)
				    {
				        PauseTrigger(trigger.Key);
				    }
				}
			}
		}

		/// <summary>
		/// Resume (un-pause) the <see cref="ITrigger" /> with the given key.
		/// </summary>
		/// <remarks>
		/// If the <see cref="ITrigger" /> missed one or more fire-times, then the
		/// <see cref="ITrigger" />'s misfire instruction will be applied.
		/// </remarks>
        public virtual void ResumeTrigger(TriggerKey triggerKey)
		{
            lock (lockObject)
            {
                TriggerWrapper tw = triggersByKey[triggerKey];

                // does the trigger exist?
                if (tw == null || tw.trigger == null)
                {
                    return;
                }

			    IOperableTrigger trig = tw.trigger;


			    // if the trigger is not paused resuming it does not make sense...
                if (tw.state != InternalTriggerState.Paused && 
                    tw.state != InternalTriggerState.PausedAndBlocked)
			    {
				    return;
			    }

				if (blockedJobs.Contains(trig.JobKey))
				{
					tw.state = InternalTriggerState.Blocked;
				}
				else
				{
                    tw.state = InternalTriggerState.Waiting;
				}

				ApplyMisfire(tw);

                if (tw.state == InternalTriggerState.Waiting)
				{
					timeTriggers.Add(tw);
				}
			}
		}

		/// <summary>
		/// Resume (un-pause) all of the <see cref="ITrigger" />s in the
		/// given group.
		/// <p>
		/// If any <see cref="ITrigger" /> missed one or more fire-times, then the
		/// <see cref="ITrigger" />'s misfire instruction will be applied.
		/// </p>
		/// </summary>
		public virtual void ResumeTriggerGroup(string groupName)
		{
            lock (lockObject)
			{
				IList<TriggerKey> keys = GetTriggerKeys(groupName);
                
				foreach (TriggerKey key in keys)
				{
				    if ((triggersByKey[key] != null))
				    {
				        string jobGroup = triggersByKey[key].jobKey.Group;
				        if (pausedJobGroups.Contains(jobGroup))
				        {
				            continue;
				        }
				    }
				    ResumeTrigger(key);
				}
				pausedTriggerGroups.Remove(groupName);
			}
		}

		/// <summary>
		/// Resume (un-pause) the <see cref="IJobDetail" /> with
		/// the given name.
		/// <p>
		/// If any of the <see cref="IJob" />'s<see cref="ITrigger" /> s missed one
		/// or more fire-times, then the <see cref="ITrigger" />'s misfire
		/// instruction will be applied.
		/// </p>
		/// </summary>
        public virtual void ResumeJob(JobKey jobKey)
		{
            lock (lockObject)
            {
                IList<IOperableTrigger> triggersForJob = GetTriggersForJob(jobKey);
                foreach (IOperableTrigger trigger in triggersForJob)
                {
                    ResumeTrigger(trigger.Key);
                }
            }
		}

		/// <summary>
		/// Resume (un-pause) all of the <see cref="IJobDetail" />s
		/// in the given group.
		/// <p>
		/// If any of the <see cref="IJob" /> s had <see cref="ITrigger" /> s that
		/// missed one or more fire-times, then the <see cref="ITrigger" />'s
		/// misfire instruction will be applied.
		/// </p>
		/// </summary>
		public virtual void ResumeJobGroup(string groupName)
		{
            lock (lockObject)
			{
			    if (pausedJobGroups.Contains(groupName))
			    {
			        pausedJobGroups.Remove(groupName);
			    }
				IList<JobKey> keys = GetJobKeys(groupName);

				foreach (JobKey key in keys)
				{
				    IList<IOperableTrigger> triggersForJob = GetTriggersForJob(key);
                    foreach (IOperableTrigger trigger in triggersForJob)
				    {
				        ResumeTrigger(trigger.Key);
				    }
				}
			}
		}

		/// <summary>
		/// Pause all triggers - equivalent of calling <see cref="PauseTriggerGroup(string)" />
		/// on every group.
		/// <p>
		/// When <see cref="ResumeAll" /> is called (to un-pause), trigger misfire
		/// instructions WILL be applied.
		/// </p>
		/// </summary>
		/// <seealso cref="ResumeAll()" /> 
		public virtual void PauseAll()
		{
            lock (lockObject)
            {
                IList<string> triggerGroupNames = GetTriggerGroupNames();

                foreach (string groupName in triggerGroupNames)
                {
                    PauseTriggerGroup(groupName);
                }
            }
		}

		/// <summary>
		/// Resume (un-pause) all triggers - equivalent of calling <see cref="ResumeTriggerGroup(string)" />
        /// on every trigger group and setting all job groups unpaused />.
		/// <p>
		/// If any <see cref="ITrigger" /> missed one or more fire-times, then the
		/// <see cref="ITrigger" />'s misfire instruction will be applied.
		/// </p>
		/// </summary>
		/// <seealso cref="PauseAll()" />
		public virtual void ResumeAll()
		{
            lock (lockObject)
			{
			    pausedJobGroups.Clear();
				IList<string> triggerGroupNames = GetTriggerGroupNames();

				foreach (string groupName in triggerGroupNames)
				{
				    ResumeTriggerGroup(groupName);
				}
			}
		}

		/// <summary>
		/// Applies the misfire.
		/// </summary>
		/// <param name="tw">The trigger wrapper.</param>
		/// <returns></returns>
		protected internal virtual bool ApplyMisfire(TriggerWrapper tw)
		{
            DateTimeOffset misfireTime = SystemTime.UtcNow();
			if (MisfireThreshold > TimeSpan.Zero)
			{
				misfireTime = misfireTime.AddMilliseconds(-1 * MisfireThreshold.TotalMilliseconds);
			}

            DateTimeOffset? tnft = tw.trigger.GetNextFireTimeUtc();
            if (!tnft.HasValue || tnft.Value > misfireTime
                || tw.trigger.MisfireInstruction == MisfireInstruction.IgnoreMisfirePolicy)
			{
				return false;
			}

			ICalendar cal = null;
			if (tw.trigger.CalendarName != null)
			{
				cal = RetrieveCalendar(tw.trigger.CalendarName);
			}

            signaler.NotifyTriggerListenersMisfired((IOperableTrigger)tw.trigger.Clone());

			tw.trigger.UpdateAfterMisfire(cal);

			if (!tw.trigger.GetNextFireTimeUtc().HasValue)
			{
                tw.state = InternalTriggerState.Complete;
                signaler.NotifySchedulerListenersFinalized(tw.trigger);
				lock (lockObject)
				{
					timeTriggers.Remove(tw);
				}
			}
			else if (tnft.Equals(tw.trigger.GetNextFireTimeUtc()))
			{
				return false;
			}

			return true;
		}

		/// <summary>
		/// Get a handle to the next trigger to be fired, and mark it as 'reserved'
		/// by the calling scheduler.
		/// </summary>
		/// <seealso cref="ITrigger" />
        public virtual IList<IOperableTrigger> AcquireNextTriggers(DateTimeOffset noLaterThan, int maxCount, TimeSpan timeWindow)
		{
			lock (lockObject)
			{
                List<IOperableTrigger> result = new List<IOperableTrigger>();

                while (true)
                {
                    TriggerWrapper tw;

                    tw = timeTriggers.First();
                    if (tw == null) return result;
                    if (!timeTriggers.Remove(tw))
                    {
                        return result;
                    }

                    if (tw.trigger.GetNextFireTimeUtc() == null)
                    {
                        continue;
                    }

                    if (ApplyMisfire(tw))
                    {
                        if (tw.trigger.GetNextFireTimeUtc() != null)
                        {
                            timeTriggers.Add(tw);
                        }
                        continue;
                    }

                    if (tw.trigger.GetNextFireTimeUtc() > noLaterThan + timeWindow)
                    {
                        timeTriggers.Add(tw);
                        return result;
                    }

                    tw.state = InternalTriggerState.Acquired;

                    tw.trigger.FireInstanceId = GetFiredTriggerRecordId();
                    IOperableTrigger trig = (IOperableTrigger)tw.trigger.Clone();
                    result.Add(trig);

                    if (result.Count == maxCount)
                    {
                        return result;
                    }
                }
            }
		}

		/// <summary>
		/// Inform the <see cref="IJobStore" /> that the scheduler no longer plans to
		/// fire the given <see cref="ITrigger" />, that it had previously acquired
		/// (reserved).
		/// </summary>
        public virtual void ReleaseAcquiredTrigger(IOperableTrigger trigger)
		{
			lock (lockObject)
			{
				TriggerWrapper tw = triggersByKey[trigger.Key];
                if (tw != null && tw.state == InternalTriggerState.Acquired)
				{
                    tw.state = InternalTriggerState.Waiting;
					timeTriggers.Add(tw);
				}
			}
		}

		/// <summary>
		/// Inform the <see cref="IJobStore" /> that the scheduler is now firing the
		/// given <see cref="ITrigger" /> (executing its associated <see cref="IJob" />),
		/// that it had previously acquired (reserved).
		/// </summary>
        public virtual IList<TriggerFiredResult> TriggersFired(IList<IOperableTrigger> triggers)
		{
		    lock (lockObject)
		    {
		        List<TriggerFiredResult> results = new List<TriggerFiredResult>();

                foreach (IOperableTrigger trigger in triggers)
		        {
		            TriggerWrapper tw = triggersByKey[trigger.Key];
		            // was the trigger deleted since being acquired?
		            if (tw == null || tw.trigger == null)
		            {
		                return null;
		            }
		            // was the trigger completed, paused, blocked, etc. since being acquired?
                    if (tw.state != InternalTriggerState.Acquired)
		            {
		                return null;
		            }

		            ICalendar cal = null;
		            if (tw.trigger.CalendarName != null)
		            {
		                cal = RetrieveCalendar(tw.trigger.CalendarName);
		                if (cal == null)
		                {
		                    return null;
                        }
		            }
                    DateTimeOffset? prevFireTime = trigger.GetPreviousFireTimeUtc();
		            // in case trigger was replaced between acquiring and firing
		            timeTriggers.Remove(tw);
		            // call triggered on our copy, and the scheduler's copy
		            tw.trigger.Triggered(cal);
		            trigger.Triggered(cal);
		            //tw.state = TriggerWrapper.STATE_EXECUTING;
                    tw.state = InternalTriggerState.Waiting;

		            TriggerFiredBundle bndle = new TriggerFiredBundle(RetrieveJob(trigger.JobKey), 
                                                                      trigger,
		                                                              cal,
		                                                              false, SystemTime.UtcNow(),
		                                                              trigger.GetPreviousFireTimeUtc(), prevFireTime,
		                                                              trigger.GetNextFireTimeUtc());

		            IJobDetail job = bndle.JobDetail;

                    if (job.ConcurrentExectionDisallowed)
		            {
		                List<TriggerWrapper> trigs = GetTriggerWrappersForJob(job.Key);
		                foreach (TriggerWrapper ttw in trigs)
		                {
                            if (ttw.state == InternalTriggerState.Waiting)
		                    {
                                ttw.state = InternalTriggerState.Blocked;
		                    }
                            if (ttw.state == InternalTriggerState.Paused)
		                    {
                                ttw.state = InternalTriggerState.PausedAndBlocked;
		                    }
		                    timeTriggers.Remove(ttw);
		                }
		                blockedJobs.Add(job.Key);
		            }
		            else if (tw.trigger.GetNextFireTimeUtc() != null)
		            {
		                lock (lockObject)
		                {
		                    timeTriggers.Add(tw);
		                }
		            }

		            results.Add(new TriggerFiredResult(bndle));
		        }
		        return results;
		    }
		}

		/// <summary> 
		/// Inform the <see cref="IJobStore" /> that the scheduler has completed the
		/// firing of the given <see cref="ITrigger" /> (and the execution its
		/// associated <see cref="IJob" />), and that the <see cref="JobDataMap" />
		/// in the given <see cref="IJobDetail" /> should be updated if the <see cref="IJob" />
		/// is stateful.
		/// </summary>
        public virtual void TriggeredJobComplete(IOperableTrigger trigger, IJobDetail jobDetail,
                                                 SchedulerInstruction triggerInstCode)
		{
			lock (lockObject)
			{
				JobWrapper jw = jobsByKey[jobDetail.Key];
				TriggerWrapper tw = triggersByKey[trigger.Key];

				// It's possible that the job is null if:
				//   1- it was deleted during execution
				//   2- RAMJobStore is being used only for volatile jobs / triggers
				//      from the JDBC job store
				if (jw != null)
				{
					IJobDetail jd = jw.jobDetail;

                    if (jobDetail.PersistJobDataAfterExecution)
                    {
                        JobDataMap newData = jobDetail.JobDataMap;
                        if (newData != null)
                        {
                            newData = (JobDataMap) newData.Clone();
                            newData.ClearDirtyFlag();
                        }
                        ((JobDetailImpl) jd).JobDataMap = newData;
                    }
                    if (jd.ConcurrentExectionDisallowed)
                    {
				        blockedJobs.Remove(jd.Key);
						List<TriggerWrapper> trigs = GetTriggerWrappersForJob(jd.Key);
						foreach (TriggerWrapper ttw in trigs)
						{
                            if (ttw.state == InternalTriggerState.Blocked)
							{
                                ttw.state = InternalTriggerState.Waiting;
								timeTriggers.Add(ttw);
							}
                            if (ttw.state == InternalTriggerState.PausedAndBlocked)
							{
                                ttw.state = InternalTriggerState.Paused;
							}
						}

                        signaler.SignalSchedulingChange(null);
					}
				}
				else
				{
					// even if it was deleted, there may be cleanup to do
					blockedJobs.Remove(jobDetail.Key);
				}

				// check for trigger deleted during execution...
				if (tw != null)
				{
					if (triggerInstCode == SchedulerInstruction.DeleteTrigger)
					{
					    log.Debug("Deleting trigger");
                        DateTimeOffset? d = trigger.GetNextFireTimeUtc();
                        if (!d.HasValue)
						{
							// double check for possible reschedule within job 
							// execution, which would cancel the need to delete...
							d = tw.trigger.GetNextFireTimeUtc();
							if (!d.HasValue)
							{
								RemoveTrigger(trigger.Key);
							}
						    else
							{
							    log.Debug("Deleting cancelled - trigger still active");
							}
						}
						else
						{
							RemoveTrigger(trigger.Key);
                            signaler.SignalSchedulingChange(null);
						}
					}
					else if (triggerInstCode == SchedulerInstruction.SetTriggerComplete)
					{
                        tw.state = InternalTriggerState.Complete;
						timeTriggers.Remove(tw);
                        signaler.SignalSchedulingChange(null);
					}
                    else if (triggerInstCode == SchedulerInstruction.SetTriggerError)
					{
						Log.Info(string.Format(CultureInfo.InvariantCulture, "Trigger {0} set to ERROR state.", trigger.Key));
                        tw.state = InternalTriggerState.Error;
                        signaler.SignalSchedulingChange(null);
					}
                    else if (triggerInstCode == SchedulerInstruction.SetAllJobTriggersError)
					{
						Log.Info(string.Format(CultureInfo.InvariantCulture, "All triggers of Job {0} set to ERROR state.", trigger.JobKey));
                        SetAllTriggersOfJobToState(trigger.JobKey, InternalTriggerState.Error);
                        signaler.SignalSchedulingChange(null);
					}
					else if (triggerInstCode == SchedulerInstruction.SetAllJobTriggersComplete)
					{
                        SetAllTriggersOfJobToState(trigger.JobKey, InternalTriggerState.Complete);
                        signaler.SignalSchedulingChange(null);
					}
				}
			}
		}

	    /// <summary>
	    /// Inform the <see cref="IJobStore" /> of the Scheduler instance's Id, 
	    /// prior to initialize being invoked.
	    /// </summary>
	    public virtual string InstanceId
	    {
	        set {  }
	    }

	    /// <summary>
	    /// Inform the <see cref="IJobStore" /> of the Scheduler instance's name, 
	    /// prior to initialize being invoked.
	    /// </summary>
        public virtual string InstanceName
	    {
	        set {  }
	    }

	    public int ThreadPoolSize
	    {
	        set { }
	    }

	    public long EstimatedTimeToReleaseAndAcquireTrigger
        {
            get { return 5; }
        }

        public bool Clustered
        {
            get {return false; }
        }

	    /// <summary>
		/// Sets the state of all triggers of job to specified state.
		/// </summary>
		protected internal virtual void SetAllTriggersOfJobToState(JobKey jobKey, InternalTriggerState state)
		{
			List<TriggerWrapper> tws = GetTriggerWrappersForJob(jobKey);
			foreach (TriggerWrapper tw in tws)
			{
				tw.state = state;
                if (state != InternalTriggerState.Waiting)
				{
					timeTriggers.Remove(tw);
				}
			}
		}

		/// <summary>
		/// Peeks the triggers.
		/// </summary>
		/// <returns></returns>
		protected internal virtual string PeekTriggers()
		{
			StringBuilder str = new StringBuilder();

			lock (lockObject)
			{
                foreach (TriggerWrapper tw in triggersByKey.Values)
				{
					str.Append(tw.trigger.Key.Name);
					str.Append("/");
				}
			}
			str.Append(" | ");

			lock (lockObject)
			{
			    foreach (TriggerWrapper tw in timeTriggers)
			    {
					str.Append(tw.trigger.Key.Name);
					str.Append("->");
				}
			}

			return str.ToString();
		}

		/// <seealso cref="IJobStore.GetPausedTriggerGroups()" />
        public virtual Collection.ISet<string> GetPausedTriggerGroups()
		{
            Collection.HashSet<string> data = new Collection.HashSet<string>(pausedTriggerGroups);
			return data;
		}
	}

	/// <summary>
	/// Comparer for triggers.
	/// </summary>
	internal class TriggerComparator : IComparer<TriggerWrapper>
	{
		public virtual int Compare(TriggerWrapper trig1, TriggerWrapper trig2)
		{
            int comp = trig1.trigger.CompareTo(trig2.trigger);
            if (comp != 0)
            {
                return comp;
            }

            comp = trig2.trigger.Priority - trig1.trigger.Priority;
            if (comp != 0)
            {
                return comp;
            }

            return trig1.trigger.Key.CompareTo(trig2.trigger.Key);
		}


	    public override bool Equals(object obj)
	    {
	        return (obj is TriggerComparator);
	    }


	    public override int GetHashCode()
	    {
	        return base.GetHashCode();
	    }
	}

	internal class JobWrapper
	{
        public readonly JobKey key;

		public IJobDetail jobDetail;

		internal JobWrapper(IJobDetail jobDetail)
		{
			this.jobDetail = jobDetail;
			key = jobDetail.Key;
		}

		public override bool Equals(object obj)
		{
			if (obj is JobWrapper)
			{
				JobWrapper jw = (JobWrapper) obj;
				if (jw.key.Equals(key))
				{
					return true;
				}
			}

			return false;
		}

		public override int GetHashCode()
		{
			return key.GetHashCode();
		}
	}

    /// <summary>
    /// Possible internal trigger states 
    /// in RAMJobStore
    /// </summary>
    public enum InternalTriggerState
    {
        /// <summary>
        /// Waiting 
        /// </summary>
        Waiting,
        /// <summary>
        /// Acquired
        /// </summary>
        Acquired,
        /// <summary>
        /// Executing
        /// </summary>
        Executing,
        /// <summary>
        /// Complete
        /// </summary>
        Complete,
        /// <summary>
        /// Paused
        /// </summary>
        Paused,
        /// <summary>
        /// Blocked
        /// </summary>
        Blocked,
        /// <summary>
        /// Paused and Blocked
        /// </summary>
        PausedAndBlocked,
        /// <summary>
        /// Error
        /// </summary>
        Error
    }

    /// <summary>
    /// Helper wrapper class
    /// </summary>
	public class TriggerWrapper : IEquatable<TriggerWrapper>
	{
		/// <summary>
		/// The key used
		/// </summary>
        public readonly TriggerKey key;

		/// <summary>
		/// Job's key
		/// </summary>
		public readonly JobKey jobKey;

		/// <summary>
		/// The trigger
		/// </summary>
        public readonly IOperableTrigger trigger;

		/// <summary>
		/// Current state
		/// </summary>
        public InternalTriggerState state = InternalTriggerState.Waiting;

        internal TriggerWrapper(IOperableTrigger trigger)
		{
			this.trigger = trigger;
			key = trigger.Key;
			jobKey = trigger.JobKey;
		}

        public bool Equals(TriggerWrapper other)
        {
            return other != null && other.key.Equals(key);
        }

        /// <summary>
        /// Determines whether the specified <see cref="T:System.Object"></see> is equal to the current <see cref="T:System.Object"></see>.
        /// </summary>
        /// <param name="obj">The <see cref="T:System.Object"></see> to compare with the current <see cref="T:System.Object"></see>.</param>
        /// <returns>
        /// true if the specified <see cref="T:System.Object"></see> is equal to the current <see cref="T:System.Object"></see>; otherwise, false.
        /// </returns>
		public override bool Equals(object obj)
		{
    	    return Equals(obj as TriggerWrapper);
		}

        /// <summary>
        /// Serves as a hash function for a particular type. <see cref="M:System.Object.GetHashCode"></see> is suitable for use in hashing algorithms and data structures like a hash table.
        /// </summary>
        /// <returns>
        /// A hash code for the current <see cref="T:System.Object"></see>.
        /// </returns>
		public override int GetHashCode()
		{
			return key.GetHashCode();
		}
	}
}
