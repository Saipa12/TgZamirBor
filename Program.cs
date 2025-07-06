using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

internal class Program
{
	private readonly Dictionary<string, int> topicDictionary = new();
	private readonly Dictionary<int, long> clientToChatMap = new();
	private readonly Dictionary<(long userId, int userMessageId), int> userToGroupMap = new();
	private readonly Dictionary<int, (long userId, int userMessageId)> groupToUserMap = new();
	private const long groupId = -1002746255386;
	private TelegramBotClient? botClient;

	private const string StateFilePath = "bot_state.json";
	private const string WelcomeMediaFile = "welcome_media.json";
	private const string MessageMapFile = "message_map.json";
	private readonly List<int> cachedWelcomePhotoIds = new();
	private bool welcomePhotosSaved = false;

	private static async Task Main(string[] args)
	{
		var program = new Program();
		await program.RunAsync();
	}

	private async Task RunAsync()
	{
		var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
		//botClient = new TelegramBotClient("7917581600:AAHX018K0PXZ2RxiPe4pYWNclgRwCNlO-Pc");
		botClient = new TelegramBotClient(token);
		LoadState();
		LoadMessageMap();

		using var cts = new CancellationTokenSource();
		botClient.StartReceiving(
			HandleUpdateAsync,
			HandlePollingErrorAsync,
			new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
			cts.Token);

		var me = await botClient.GetMe();
		Console.WriteLine($"🤖 Бот запущен: @{me.Username}");

		Console.ReadLine();
		cts.Cancel();
	}

	private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
	{
		if (update.Message is { } message)
		{
			if (!welcomePhotosSaved && !File.Exists(WelcomeMediaFile))
			{
				if (message.Chat.Id == groupId && message.MessageThreadId is null && message.Photo is not null)
				{
					cachedWelcomePhotoIds.Add(message.MessageId);
					Console.WriteLine($"📸 Добавлено фото messageId: {message.MessageId}");
				}
				else if (message.Chat.Id == groupId && message.MessageThreadId is null && message.Text == "#done")
				{
					SaveWelcomePhotoIds(cachedWelcomePhotoIds);
					welcomePhotosSaved = true;
					Console.WriteLine("✅ Приветственные фото сохранены");
				}
				return;
			}

			if (message.Chat.Type == ChatType.Private && message.Text == "/start")
			{
				await botClient.SendMessage(message.Chat.Id, "👋 Привет! Вот информация для старта:", cancellationToken: cancellationToken);
				foreach (var id in LoadWelcomePhotoIds())
				{
					await botClient.CopyMessage(message.Chat.Id, groupId, id, cancellationToken: cancellationToken);
				}
				return;
			}

			if (message.Chat.Type == ChatType.Private)
			{
				string topicName = $"{message.From?.FirstName} {message.From?.LastName}".Trim();
				if (!topicDictionary.TryGetValue(topicName, out int topicId))
				{
					var forum = await botClient.CreateForumTopic(groupId, topicName, cancellationToken: cancellationToken);
					topicId = forum.MessageThreadId;
					topicDictionary[topicName] = topicId;
					clientToChatMap[topicId] = message.Chat.Id;
					SaveState();
				}

				int? replyTo = null;

				// Проверяем, является ли сообщение ответом
				if (message.ReplyToMessage is { } reply)
				{
					// Пробуем найти исходное сообщение пользователя в теме
					if (userToGroupMap.TryGetValue((message.Chat.Id, reply.MessageId), out int replyId))
					{
						replyTo = replyId;
					}
					else
					{
						// Сообщение, на которое ответили, отправлено ботом — копируем его вручную в тему
						var copied = await botClient.CopyMessage(
							chatId: groupId,
							fromChatId: message.Chat.Id,
							messageId: reply.MessageId,
							messageThreadId: topicId,
							cancellationToken: cancellationToken);

						replyId = copied.Id;
						var sentCopied = await botClient.SendMessage(
							chatId: groupId,
							text: message.Text ?? "[медиа]",
							messageThreadId: topicId,
							replyParameters: replyTo,
							cancellationToken: cancellationToken);

						// Сохраняем связь для будущих правок
						userToGroupMap[(message.Chat.Id, sentCopied.MessageId)] = sentCopied.Id;
						groupToUserMap[sentCopied.Id] = (message.Chat.Id, sentCopied.MessageId);
						SaveMessageMap();

						replyTo = sentCopied.Id;
					}
				}

				var sent = await botClient.SendMessage(
					chatId: groupId,
					text: message.Text ?? "[медиа]",
					messageThreadId: topicId,
					replyParameters: replyTo,
					cancellationToken: cancellationToken);

				userToGroupMap[(message.Chat.Id, message.MessageId)] = sent.MessageId;
				groupToUserMap[sent.MessageId] = (message.Chat.Id, message.MessageId);
				SaveMessageMap();
				return;
			}
			else if (message.Chat.Id == groupId && message.ReplyToMessage?.From?.Id == botClient.BotId)
			{
				if (message.MessageThreadId is int threadId && clientToChatMap.TryGetValue(threadId, out var clientId))
				{
					int? replyTo = null;
					if (message.ReplyToMessage is { } reply && groupToUserMap.TryGetValue(reply.MessageId, out var target))
						replyTo = target.userMessageId;

					var sent = await botClient.SendMessage(
						clientId,
						message.Text ?? "[медиа]",
						replyParameters: replyTo,
						cancellationToken: cancellationToken);

					userToGroupMap[(clientId, sent.MessageId)] = message.MessageId;
					groupToUserMap[message.MessageId] = (clientId, sent.MessageId);
					SaveMessageMap();
				}
				return;
			}
		}

		if (update.EditedMessage is { } edited)
		{
			if (edited.Chat.Type == ChatType.Private && userToGroupMap.TryGetValue((edited.Chat.Id, edited.MessageId), out int groupMsgId))
			{
				await botClient.EditMessageText(groupId, groupMsgId, edited.Text ?? "[изменено]", cancellationToken: cancellationToken);
			}
			else if (edited.Chat.Id == groupId && groupToUserMap.TryGetValue(edited.MessageId, out var userTarget))
			{
				await botClient.EditMessageText(userTarget.userId, userTarget.userMessageId, edited.Text ?? "[изменено]", cancellationToken: cancellationToken);
			}
			return;
		}

		if (update.Message is { } deleted && deleted.Text == "/delete")
		{
			if (deleted.Chat.Type == ChatType.Private && userToGroupMap.TryGetValue((deleted.Chat.Id, deleted.MessageId - 1), out int gId))
			{
				await DeleteMessage(groupId, gId);
			}
			else if (deleted.Chat.Id == groupId && groupToUserMap.TryGetValue(deleted.MessageId - 1, out var userTarget))
			{
				await DeleteMessage(userTarget.userId, userTarget.userMessageId);
			}
		}
	}

	private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken cancellationToken)
	{
		Console.WriteLine(exception is ApiRequestException apiEx
			? $"Telegram API Error: [{apiEx.ErrorCode}] {apiEx.Message}"
			: exception.ToString());
		return Task.CompletedTask;
	}

	private void LoadState()
	{
		if (!File.Exists(StateFilePath)) return;
		var state = JsonSerializer.Deserialize<BotState>(File.ReadAllText(StateFilePath));
		if (state is null) return;
		topicDictionary.Clear();
		clientToChatMap.Clear();
		foreach (var kv in state.Topics) topicDictionary[kv.Key] = kv.Value;
		foreach (var kv in state.Clients) clientToChatMap[kv.Key] = kv.Value;
		Console.WriteLine("✅ Состояние загружено");
	}

	private void SaveState()
	{
		var state = new BotState { Topics = topicDictionary, Clients = clientToChatMap };
		File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
	}

	private void SaveWelcomePhotoIds(List<int> ids)
	{
		File.WriteAllText(WelcomeMediaFile, JsonSerializer.Serialize(new WelcomeMedia { PhotoIds = ids }, new JsonSerializerOptions { WriteIndented = true }));
	}

	private List<int> LoadWelcomePhotoIds()
	{
		if (!File.Exists(WelcomeMediaFile)) return new();
		var data = JsonSerializer.Deserialize<WelcomeMedia>(File.ReadAllText(WelcomeMediaFile));
		return data?.PhotoIds ?? new();
	}

	private void SaveMessageMap()
	{
		var map = new MessageMap { UserToGroup = userToGroupMap, GroupToUser = groupToUserMap };
		File.WriteAllText(MessageMapFile, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
	}

	private void LoadMessageMap()
	{
		if (!File.Exists(MessageMapFile)) return;
		var map = JsonSerializer.Deserialize<MessageMap>(File.ReadAllText(MessageMapFile));
		if (map is null) return;
		foreach (var kv in map.UserToGroup) userToGroupMap[kv.Key] = kv.Value;
		foreach (var kv in map.GroupToUser) groupToUserMap[kv.Key] = kv.Value;
	}

	public async Task DeleteMessage(long chatId, int messageId)
	{
		await botClient!.DeleteMessage(chatId, messageId);
	}
}

public class BotState
{
	public Dictionary<string, int> Topics { get; set; } = new();
	public Dictionary<int, long> Clients { get; set; } = new();
}

public class WelcomeMedia
{
	public List<int> PhotoIds { get; set; } = new();
}

public class MessageMap
{
	public Dictionary<(long userId, int userMessageId), int> UserToGroup { get; set; } = new();
	public Dictionary<int, (long userId, int userMessageId)> GroupToUser { get; set; } = new();
}