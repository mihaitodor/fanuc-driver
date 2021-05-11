﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using fanuc.veneers;
using MQTTnet;
using MQTTnet.Client.Options;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace fanuc
{
    class Program
    {
        private static bool MQTT_CONNECT = false;
        private static bool MQTT_PUBLISH_STATUS = false;
        private static bool MQTT_PUBLISH_ARRIVALS = false;
        private static bool MQTT_PUBLISH_CHANGES = false;

        private class MQTTDisco
        {
            private struct MQTTDiscoItem
            {
                public long added;
                public string machineId;
                public string arrivalTopic;
                public string changeTopic;
            }
            
            private Dictionary<string, MQTTDiscoItem> _items = new Dictionary<string, MQTTDiscoItem>();

            private dynamic _mqtt;
            
            public MQTTDisco(dynamic mqtt)
            {
                _mqtt = mqtt;
            }

            public void Add(string machineId)
            {
                if (!_items.ContainsKey(machineId))
                {
                    _items.Add(machineId, new MQTTDiscoItem()
                    {
                        machineId = machineId,
                        added = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds(),
                        arrivalTopic = $"fanuc/{machineId}-all",
                        changeTopic = $"fanuc/{machineId}"
                    });

                    string topic = "fanuc/DISCO";
                    string payload_string = JObject.FromObject(_items).ToString();

                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()} DISCO {payload_string.Length}b => {topic}");
                
                    var msg = new MqttApplicationMessageBuilder()
                        .WithRetainFlag(true)
                        .WithTopic(topic)
                        .WithPayload(payload_string)
                        .Build();
                    
                    if (MQTT_CONNECT && MQTT_PUBLISH_STATUS)
                    {
                        var r = _mqtt.PublishAsync(msg, CancellationToken.None).Result;
                    }
                }
            }
        }

        static void Main(string[] args)
        {
            string config_file = getArgument(args, "--config", "config.yml");
            dynamic config = readConfig(config_file);
            dynamic mqtt = setupMqtt(config);
            dynamic machines = createMachines(config, mqtt);
            createVeneers(machines);
            processVeneers(machines, mqtt);
        }

        static string getArgument(string[] args, string option, string defaultValue)
        {
            var value = args.SkipWhile(i => i != option).Skip(1).Take(1).FirstOrDefault();
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }
        
        static dynamic readConfig(string config_file)
        {
            var input = new StreamReader(config_file);

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            return deserializer.Deserialize(input);
        }
        
        static dynamic setupMqtt(dynamic config)
        {
            MQTT_CONNECT = config["broker"]["enabled"];
            MQTT_PUBLISH_STATUS = config["broker"]["publish_status"];
            MQTT_PUBLISH_ARRIVALS = config["broker"]["publish_arrivals"];
            MQTT_PUBLISH_CHANGES = config["broker"]["publish_changes"];
            
            var factory = new MqttFactory();
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(config["broker"]["net_ip"], config["broker"]["net_port"])
                .Build();
            var client = factory.CreateMqttClient();
            if (MQTT_CONNECT)
            {
                var r = client.ConnectAsync(options, CancellationToken.None).Result;
            }

            return client;
        }

        static dynamic createMachines(dynamic config, dynamic mqtt)
        {
            var machine_confs = new List<dynamic>();

            foreach (dynamic machine_conf in config["machines"])
            {
                machine_confs.Add(new
                {
                    enabled = machine_conf["enabled"],
                    id = machine_conf["id"],
                    ip = machine_conf["net_ip"],
                    port = machine_conf["net_port"],
                    timeout = machine_conf["net_timeout_s"],
                    collector = machine_conf["strategy_type"],
                    collectorSweepMs = machine_conf["sweep_ms"]
                });
            }

            Action<Veneers, Veneer> on_data_arrival = (vv, v) =>
            {
                dynamic payload = new
                {
                    observation = new
                    {
                        time =  new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds(),
                        machine = vv.Machine.Id,
                        name = v.Name,
                        marker = v.Marker
                    },
                    source = new
                    {
                        method = v.LastArrivedInput.method,
                        v.LastArrivedInput.invocationMs,
                        data = v.LastArrivedInput.request.GetType().GetProperty(v.LastArrivedInput.method).GetValue(v.LastArrivedInput.request, null)
                    },
                    delta = new
                    {
                        time = v.ArrivalDelta,
                        data = v.LastArrivedValue
                    }
                };

                var topic = $"fanuc/{vv.Machine.Id}-all/{v.Name}{(v.SliceKey == null ? string.Empty : "/" + v.SliceKey.ToString())}";
                string payload_string = JObject.FromObject(payload).ToString();

                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine($"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()} ARRIVE {payload_string.Length}b => {topic}");
                
                var msg = new MqttApplicationMessageBuilder()
                    .WithRetainFlag(true)
                    .WithTopic(topic)
                    .WithPayload(payload_string)
                    .Build();
                if (MQTT_CONNECT && MQTT_PUBLISH_ARRIVALS)
                {
                    var r = mqtt.PublishAsync(msg, CancellationToken.None);
                }
            };
            
            Action<Veneers, Veneer> on_data_change = (vv, v) =>
            {
                dynamic payload = new
                {
                    observation = new
                    {
                        time =  new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds(),
                        machine = vv.Machine.Id,
                        name = v.Name,
                        marker = v.Marker
                    },
                    source = new
                    {
                        method = v.LastChangedInput.method,
                        v.LastChangedInput.invocationMs,
                        data = v.LastChangedInput.request.GetType().GetProperty(v.LastChangedInput.method).GetValue(v.LastChangedInput.request, null)
                    },
                    delta = new
                    {
                        time = v.ChangeDelta,
                        data = v.LastChangedValue
                    }
                };

                var topic = $"fanuc/{vv.Machine.Id}/{v.Name}{(v.SliceKey == null ? string.Empty : "/" + v.SliceKey.ToString())}";
                string payload_string = JObject.FromObject(payload).ToString();
                
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"{new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds()} CHANGE {payload_string.Length}b => {topic}");
                //Console.WriteLine(payload_string);
                //Console.WriteLine();
                
                var msg = new MqttApplicationMessageBuilder()
                    .WithRetainFlag(true)
                    .WithTopic(topic)
                    .WithPayload(payload_string)
                    .Build();
                if (MQTT_CONNECT && MQTT_PUBLISH_CHANGES)
                {
                    var r = mqtt.PublishAsync(msg, CancellationToken.None);
                }
            };

            Action<Veneers, Veneer> on_error = (vv, v) =>
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(new { method = v.LastArrivedInput.method, rc = v.LastArrivedInput.rc });
            };

            dynamic machines = new Machines();
            
            foreach (var cfg in machine_confs)
            {
                var machine = machines.Add(cfg.enabled, cfg.id, cfg.ip, (ushort)cfg.port, (short)cfg.timeout);
                machine.AddCollector(Type.GetType(cfg.collector), cfg.collectorSweepMs);
                machine.Veneers.OnDataArrival = on_data_arrival;
                machine.Veneers.OnDataChange = on_data_change;
                machine.Veneers.OnError = on_error;
            }

            return machines;
        }
        
        static void createVeneers(dynamic machines)
        {
            foreach (var machine in machines[null])
            {
                machine.InitCollector();
            }
        }
        
        static void processVeneers(dynamic machines, dynamic mqtt)
        {
            while (true)
            {
                Thread.Sleep(1000);
                
                foreach (var machine in machines[null])
                {
                    machine.RunCollector();
                    
                    dynamic payload = new
                    {
                        observation = new
                        {
                            time =  new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds(),
                            machine = machine.Id,
                            name = "PING"
                        },
                        source = new
                        {
                            data = machine.Info
                        },
                        delta = new
                        {
                            data = machine.CollectorSuccess ? "OK" : "NOK"
                        }
                    };
                        
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic($"fanuc/{machine.Id}/PING")
                        .WithPayload(JObject.FromObject(payload).ToString())
                        .WithRetainFlag()
                        .Build();
                    if (MQTT_CONNECT && MQTT_PUBLISH_STATUS)
                    {
                        var r = mqtt.PublishAsync(msg, CancellationToken.None).Result;
                    }
                }
            }
        }
    }
}      