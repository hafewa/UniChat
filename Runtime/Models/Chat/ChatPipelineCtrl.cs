using System;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Kurisu.UniChat.LLMs;
using Kurisu.UniChat.Memory;
using Kurisu.UniChat.NLP;
using Newtonsoft.Json;
using Unity.Sentis;
using UnityEngine.Assertions;
using UnityEngine.Pool;
namespace Kurisu.UniChat
{
    public class PipelineConfig
    {
        /// <summary>
        /// Run pipeline on which backend
        /// </summary>
        public BackendType backendType = BackendType.GPUCompute;
        /// <summary>
        /// Log pipeline status
        /// </summary>
        public bool verbose = false;
        /// <summary>
        /// Whether write data to source table and dataBase
        /// </summary>
        public bool canWrite = true;
        /// <summary>
        /// Pipeline input score threshold
        /// </summary>
        public float inputThreshold = 0.85f;
        /// <summary>
        /// Pipeline output score threshold
        /// </summary>
        public float outputThreshold = 0.85f;
        public static PipelineConfig Default = new()
        {
            backendType = BackendType.GPUCompute,
            canWrite = true,
            verbose = false,
            inputThreshold = 0.85f,
            outputThreshold = 0.85f
        };
    }
    public class InputGenerationRequest
    {
        public GenerateContext generateContext;
        public UniTaskCompletionSource<bool> waitSource;
        public InputGenerationRequest(GenerateContext generateContext)
        {
            this.generateContext = generateContext;
            waitSource = new();
        }
        public void SetResult(string generatedContent)
        {
            generateContext.generatedContent = generatedContent;
            waitSource?.TrySetResult(true);
            waitSource = null;
        }
        public void Cancel()
        {
            waitSource?.TrySetResult(false);
            waitSource = null;
        }
    }
    public static class ChatGeneratorIds
    {
        public const int Input = 0;
        public const int ChatGPT = 1;
        public const int Oobabooga = 2;
    }
    public class ChatPipelineCtrl<TPipeline, KTable> : IDisposable
    where TPipeline : ChatPipeline, new()
    where KTable : ISerializable, IEmbeddingTable, new()
    {
        public TextEncoder Encoder { get; protected set; }
        public ChatDataBase DataBase { get; protected set; }
        public KTable Table { get; protected set; }
        public ChatModelFile ChatFile { get; protected set; }
        public ISplitter Splitter { get; protected set; }
        public IGenerator Generator { get; protected set; }
        public TPipeline Pipeline { get; protected set; }
        public ChatHistory History { get; } = new();
        public ChatMemory Memory { get; protected set; }
        public ILLMSettings LLMSettings { get; protected set; }
        public string Context { get => Memory.Context; set => Memory.Context = value; }
        public string UserName { get => Memory.UserName; set => Memory.UserName = value; }
        public string BotName { get => Memory.BotName; set => Memory.BotName = value; }
        public event Action<InputGenerationRequest> OnCallGeneration;
        private readonly Dictionary<int, IGenerator> generatorMap = new();
        public ChatPipelineCtrl(ChatModelFile chatFile, ILLMSettings llmSettings)
        {
            ChatFile = chatFile;
            var graphPath = ChatFile.GraphPath;
            if (File.Exists(graphPath))
            {
                DataBase = new(graphPath);
            }
            else
            {
                DataBase = new(ChatFile.embeddingDim);
            }
            var tablePath = ChatFile.TablePath;
            Table = new();
            if (File.Exists(tablePath))
            {
                Table.Load(tablePath);
            }
            LLMSettings = llmSettings;
            Splitter = CreateSplitter(chatFile.splitter, chatFile.splitterPattern);
            Generator = generatorMap[-1] = new InputGenerator(OnInputGeneration);
            Assert.IsNotNull(Splitter);
            Memory = CreateMemory(chatFile.memory, chatFile.memoryPattern);
            Assert.IsNotNull(Memory);
            Memory.ChatHistory = History;
        }
        public static ISplitter CreateSplitter(string splitter, string pattern)
        {
            Type splitterType = splitter switch
            {
                nameof(SlidingWindowSplitter) => typeof(SlidingWindowSplitter),
                nameof(RegexSplitter) => typeof(RegexSplitter),
                nameof(RecursiveCharacterTextSplitter) => typeof(RecursiveCharacterTextSplitter),
                _ => throw new ArgumentOutOfRangeException(nameof(splitterType)),
            };
            if (!string.IsNullOrEmpty(pattern))
                return JsonConvert.DeserializeObject(pattern, splitterType) as ISplitter;
            else
                return Activator.CreateInstance(splitterType) as ISplitter;
        }
        public static ChatMemory CreateMemory(string memory, string pattern)
        {
            Type memoryType = memory switch
            {
                nameof(ChatBufferMemory) => typeof(ChatBufferMemory),
                nameof(ChatWindowBufferMemory) => typeof(ChatWindowBufferMemory),
                _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
            };
            if (!string.IsNullOrEmpty(pattern))
                return JsonConvert.DeserializeObject(pattern, memoryType) as ChatMemory;
            else
                return Activator.CreateInstance(memoryType) as ChatMemory;
        }
        /// <summary>
        /// Set and save splitter
        /// </summary>
        /// <param name="splitter"></param>
        public void SetSplitter(ISplitter splitter)
        {
            Splitter = splitter;
            ChatFile.splitter = splitter.GetType().Name;
            ChatFile.splitterPattern = JsonConvert.SerializeObject(splitter);
        }
        /// <summary>
        /// Save and set memory
        /// </summary>
        /// <param name="memory"></param>
        public void SetMemory(ChatMemory memory)
        {
            Memory = memory;
            Memory.ChatHistory = History;
            ChatFile.memory = memory.GetType().Name;
            ChatFile.memoryPattern = JsonConvert.SerializeObject(memory);
        }
        /// <summary>
        /// Initialize pipeline if change properties
        /// </summary>
        /// <param name="config"></param>
        /// <returns></returns>
        public virtual async UniTask InitializePipeline(PipelineConfig config = null)
        {
            config ??= PipelineConfig.Default;
            Encoder?.Dispose();
            Pipeline?.Dispose();
            ModelProvider provider = ModelProviderFactory.Instance.Create(ChatFile.modelProvider);
            Encoder = new TextEncoder(
                await provider.LoadModel(ChatFile.ModelPath),
                new BertTokenizer(await provider.LoadTokenizer(ChatFile.TokenizerPath)),
                config.backendType
            );
            Pipeline = new TPipeline()
                            .SetBackend(config.backendType)
                            .SetInputConvertor(new ChatPipeline.ContextConverter(Encoder, Memory))
                            .SetOutputConvertor(new MultiEncoderConverter(Encoder))
                            .SetGenerator(Generator)
                            .SetMemory(Memory)
                            .SetSource(Table)
                            .SetEmbedding(DataBase)
                            .SetPersister(config.canWrite ? new TextEmbeddingTable.PersistHandler() : null)
                            .SetTemperature(config.outputThreshold)
                            .SetVerbose(config.verbose)
                            .SetFilter(new TopSimilarityFilter(config.inputThreshold)) as TPipeline;
        }
        public void ReleasePipeline()
        {
            Pipeline?.Dispose();
            Pipeline = null;
        }
        public void Dispose()
        {
            ReleasePipeline();
            Encoder?.Dispose();
            DataBase?.Dispose();
        }
        /// <summary>
        /// Run pipeline using history context
        /// </summary>
        /// <returns></returns>
        public async UniTask<GenerateContext> RunPipeline()
        {
            var pool = ListPool<string>.Get();
            Splitter.Split(Memory.GetMemoryContext(), pool);
            var context = new GenerateContext(pool);
            try
            {
                await Pipeline.Run(context);
                return context;
            }
            finally
            {
                ListPool<string>.Release(pool);
            }
        }
        /// <summary>
        /// Run pipeline using history context and new input
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        public async UniTask<GenerateContext> RunPipeline(string input)
        {
            var pool = ListPool<string>.Get();
            History.AppendUserMessage(input);
            Splitter.Split(Memory.GetMemoryContext(), pool);
            var context = new GenerateContext(pool);
            try
            {
                await Pipeline.Run(context);
                return context;
            }
            finally
            {
                //This message is temporary and should be added manually after pipeline
                History.RemoveLastUserMessage();
                ListPool<string>.Release(pool);
            }
        }
        public void SwitchGenerator(int generatorId, bool forceNewGenerator)
        {
            if (generatorId == ChatGeneratorIds.Input)
            {
                SwitchInputGenerator();
            }
            else
            {
                var llmType = generatorId switch
                {
                    ChatGeneratorIds.ChatGPT => LLMType.ChatGPT,
                    ChatGeneratorIds.Oobabooga => LLMType.Oobabooga,
                    _ => throw new ArgumentOutOfRangeException()
                };
                SwitchLLMGenerator(llmType, forceNewGenerator);
            }
        }
        private IGenerator SwitchLLMGenerator(LLMType llmType, bool forceNewGenerator)
        {
            int id = (int)llmType;
            if (forceNewGenerator || !generatorMap.TryGetValue(id, out var generator))
            {
                generator = generatorMap[id] = new LLMGenerator(LLMFactory.Create(llmType, LLMSettings), Memory);
            }
            return Generator = generator;
        }
        private IGenerator SwitchInputGenerator()
        {
            return Generator = generatorMap[-1];
        }
        private UniTaskCompletionSource<bool> OnInputGeneration(GenerateContext generateContext)
        {
            var request = new InputGenerationRequest(generateContext);
            OnCallGeneration?.Invoke(request);
            return request.waitSource;
        }
        public void SaveModel()
        {
            if (!Directory.Exists(ChatFile.DirectoryPath))
            {
                Directory.CreateDirectory(ChatFile.DirectoryPath);
            }
            File.WriteAllText(ChatFile.ConfigPath, JsonConvert.SerializeObject(ChatFile, Formatting.Indented));
            Table.Save(ChatFile.TablePath);
            DataBase.Save(ChatFile.GraphPath);
        }
        public void SaveSession(string filePath)
        {
            File.WriteAllText(filePath, JsonConvert.SerializeObject(History.SaveSession(), Formatting.Indented));
        }
        public bool LoadSession(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return false;
            }
            var session = JsonConvert.DeserializeObject<ChatSession>(File.ReadAllText(filePath));
            History.LoadSession(session); ;
            return true;
        }
        public bool LoadSession(ChatSession chatSession)
        {
            History.LoadSession(chatSession);
            return true;
        }
    }
    /// <summary>
    /// Default pipeline ctrl for <see cref="TextEmbeddingTable"/>
    /// </summary>
    public class ChatPipelineCtrl : ChatPipelineCtrl<ChatPipeline, TextEmbeddingTable>
    {
        public ChatPipelineCtrl(ChatModelFile chatFile, ILLMSettings llmSettings) : base(chatFile, llmSettings)
        {
        }
    }
}