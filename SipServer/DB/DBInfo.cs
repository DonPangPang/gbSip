﻿using GB28181.XML;
using SipServer.Models;
using SQ.Base;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SipServer.DB
{
    public class DBInfo
    {
        SipServer sipServer;
        /// <summary>
        /// Redis操作类
        /// </summary>
        RedisHelp.RedisHelper RedisHelper;

        T TryParseJSON<T>(string strjson)
        {
            try
            {
                return strjson.ParseJSON<T>();
            }
            catch
            {
                return default(T);
            }
        }
        string GetDevInfoHead(string DeviceID)
        {
            return RedisConstant.DevInfoHead + DeviceID;
        }
        public DBInfo(SipServer sipServer)
        {
            this.sipServer = sipServer;
            RedisHelper = new RedisHelp.RedisHelper(-1, sipServer.Settings.RedisExchangeHosts);
            RedisHelper.SetSysCustomKey("");
        }
        /// <summary>
        /// 保存连接状态
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <param name="Status"></param>
        /// <returns></returns>
        public Task<bool> SaveConnStatus(string DeviceID, ConnStatus Status)
        {
            return RedisHelper.HashSetAsync(GetDevInfoHead(DeviceID), RedisConstant.StatusKey, Status);
        }
        /// <summary>
        /// 保存获取的设备状态
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <param name="deviceStatus"></param>
        /// <returns></returns>
        public Task<bool> SaveDeviceStatus(string DeviceID, DeviceStatus deviceStatus)
        {
            return RedisHelper.HashSetAsync(GetDevInfoHead(DeviceID), RedisConstant.DeviceStatusKey, deviceStatus);
        }
        /// <summary>
        /// 一次获取设备、通道、状态和连接状态信息
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <returns></returns>
        public async Task<(DeviceInfo, List<Catalog.Item>, DeviceStatus, ConnStatus)> GetDevAll(string DeviceID)
        {
            (DeviceInfo, List<Catalog.Item>, DeviceStatus, ConnStatus) ret = default;
            var gbdevs = await RedisHelper.HashGetAllAsync(RedisConstant.DevInfoHead + DeviceID);
            foreach (var entry in gbdevs)
            {
                if (entry.Name == RedisConstant.DeviceInfoKey && entry.Value.HasValue)
                {
                    ret.Item1 = TryParseJSON<DeviceInfo>(entry.Value);
                }
                else if (entry.Name == RedisConstant.ChannelsKey && entry.Value.HasValue)
                {
                    ret.Item2 = TryParseJSON<List<Catalog.Item>>(entry.Value);
                }
                else if (entry.Name == RedisConstant.DeviceStatusKey && entry.Value.HasValue)
                {
                    ret.Item3 = TryParseJSON<DeviceStatus>(entry.Value);
                }
                else if (entry.Name == RedisConstant.StatusKey)
                {
                    ret.Item4 = TryParseJSON<ConnStatus>(entry.Value);
                }
            }
            return ret;
        }


        #region Channel
        /// <summary>
        /// 获取设备通道列表
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <returns></returns>
        public Task<List<Catalog.Item>> GetChannelList(string DeviceID)
        {
            return SafeHashGetAsync<List<Catalog.Item>>(GetDevInfoHead(DeviceID), RedisConstant.ChannelsKey);
        }
        Task<T> SafeHashGetAsync<T>(string key, string dataKey)
        {
            return RedisHelper.GetDatabase().HashGetAsync(key, dataKey).ContinueWith<T>(p =>
              {
                  if (p.Result.HasValue)
                  {
                      return SQ.Base.JsonHelper.ParseJSON<T>(p.Result);
                  }
                  return default(T);
              });
        }

        /// <summary>
        /// 保存通道
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <param name="lst"></param>
        /// <returns></returns>
        public Task<bool> SaveChannels(string DeviceID, List<Catalog.Item> lst, bool SaveDeviceIdsKey = false)
        {
            if (SaveDeviceIdsKey)
            {
                var db = RedisHelper.GetDatabase();
                var bat = db.CreateTransaction();
                bat.HashSetAsync(GetDevInfoHead(DeviceID), hashField: RedisConstant.ChannelsKey, value: lst.ToJson());
                bat.SortedSetAddAsync(RedisConstant.DeviceIdsKey, DeviceID, Convert.ToDouble(DeviceID));
                return bat.ExecuteAsync();
            }
            else
                return RedisHelper.HashSetAsync(GetDevInfoHead(DeviceID), RedisConstant.ChannelsKey, lst);
        }
        #endregion

        #region DeviceInfo
        /// <summary>
        /// 获取设备信息
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <returns></returns>
        public Task<DeviceInfo> GetDeviceInfo(string DeviceID)
        {
            return SafeHashGetAsync<DeviceInfo>(GetDevInfoHead(DeviceID), RedisConstant.DeviceInfoKey);
        }
        /// <summary>
        /// 获取设备列表(支持分页)
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public async Task<List<DeviceInfoExt>> GetDeviceInfoList(long start = 0, long end = -1)
        {
            List<DeviceInfoExt> lstDev = new List<DeviceInfoExt>();
            var db = RedisHelper.GetDatabase();
            var lst = await db.SortedSetRangeByRankAsync(RedisConstant.DeviceIdsKey, start, end);
            if (lst.Length > 0)
            {
                Dictionary<string, Task<StackExchange.Redis.RedisValue>> ditDeviceInfo = new Dictionary<string, Task<StackExchange.Redis.RedisValue>>();
                Dictionary<string, Task<StackExchange.Redis.RedisValue>> ditStatus = new Dictionary<string, Task<StackExchange.Redis.RedisValue>>();
                var bat = db.CreateBatch();
                foreach (var id in lst)
                {
                    ditDeviceInfo[id] = bat.HashGetAsync(RedisConstant.DevInfoHead + id, RedisConstant.DeviceInfoKey);
                    ditStatus[id] = bat.HashGetAsync(RedisConstant.DevInfoHead + id, RedisConstant.StatusKey);
                }
                bat.Execute();
                foreach (var item in ditDeviceInfo)
                {
                    DeviceInfo dev;
                    if (item.Value.Result.HasValue)
                    {
                        dev = item.Value.Result.ToString().ParseJSON<DeviceInfo>();
                    }
                    else
                    {
                        dev = new DeviceInfo { DeviceID = item.Key };
                    }
                    var status = ditStatus[dev.DeviceID].Result.ToString().ParseJSON<ConnStatus>();
                    if (sipServer.TryGetClient(dev.DeviceID, out var client))
                    {
                        status.Online = true;
                        status.KeepAliveTime = client.Status.KeepAliveTime;
                    }
                    else
                    {
                        status.Online = false;
                    }
                    lstDev.Add(new DeviceInfoExt
                    {
                        Device = dev,
                        Status = status,
                        RemoteEndPoint = client?.RemoteEndPoint,
                    });
                }
            }

            return lstDev;
        }
        /// <summary>
        /// 删除设备信息
        /// </summary>
        /// <param name="DeviceID"></param>
        /// <param name="removeClient"></param>
        /// <returns></returns>
        public Task<bool> DeleteDeviceInfo(string DeviceID, bool removeClient = true)
        {
            if (removeClient)
                sipServer.RemoveClient(DeviceID, false);
            var db = RedisHelper.GetDatabase();
            var bat = db.CreateTransaction();
            bat.KeyDeleteAsync(RedisConstant.DevInfoHead + DeviceID);
            bat.SortedSetRemoveAsync(RedisConstant.DeviceIdsKey, DeviceID);
            return bat.ExecuteAsync();
        }
        /// <summary>
        /// 仅获取设备ID(支持分页)
        /// </summary>
        /// <param name="start"></param>
        /// <param name="end"></param>
        /// <returns></returns>
        public Task<List<string>> GetDeviceIds(long start = 0, long end = -1)
        {
            return RedisHelper.GetDatabase().SortedSetRangeByRankAsync(RedisConstant.DeviceIdsKey, start, end).ContinueWith(lst =>
            {
                return lst.Result.Select(p => p.ToString()).ToList();
            }); ;
        }
        /// <summary>
        /// 保存设备信息
        /// </summary>
        /// <param name="deviceInfo"></param>
        /// <returns></returns>
        public Task<bool> SaveDeviceInfo(DeviceInfo deviceInfo)
        {
            var db = RedisHelper.GetDatabase();
            var bat = db.CreateTransaction();
            bat.HashSetAsync(GetDevInfoHead(deviceInfo.DeviceID), hashField: RedisConstant.DeviceInfoKey, deviceInfo.ToJson());
            bat.SortedSetAddAsync(RedisConstant.DeviceIdsKey, deviceInfo.DeviceID, Convert.ToDouble(deviceInfo.DeviceID));
            return bat.ExecuteAsync();
        }
        #endregion


        #region SuperiorInfo
        public Task<SuperiorInfo> GetSuperiorInfo(string id)
        {
            return SafeHashGetAsync<SuperiorInfo>(RedisConstant.SuperiorKey, id);
        }
        public async Task<List<SuperiorInfoEx>> GetSuperiorList()
        {
            List<SuperiorInfoEx> lst = new List<SuperiorInfoEx>();
            var superiors = await RedisHelper.HashGetAllAsync(RedisConstant.SuperiorKey);
            foreach (var item in superiors)
            {
                if (item.Value.HasValue)
                {
                    var info = TryParseJSON<SuperiorInfoEx>(item.Value);
                    info.Client = sipServer.Cascade.GetClient(info.ID);
                    lst.Add(info);
                }
            }
            return lst;
        }
        public Task<bool> SaveSuperior(SuperiorInfo sinfo)
        {
            return RedisHelper.HashSetAsync(RedisConstant.SuperiorKey, sinfo.ID, sinfo);
        }
        public Task<bool> DeleteSuperior(string id)
        {
            return RedisHelper.HashDeleteAsync(RedisConstant.SuperiorKey, id);
        }
        #endregion
    }
}
