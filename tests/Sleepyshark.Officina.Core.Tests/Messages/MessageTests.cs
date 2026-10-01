using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Tests.Messages;

public class MessageTests
{
    private static readonly IEnumerable<Type> MessageTypes = typeof(Message).Assembly.GetExportedTypes()
        .Where(type => type.Namespace == typeof(Message).Namespace && !type.IsEnum);

    [Fact]
    public void A_message_has_a_role_and_content()
    {
        var message = Message.User("hello");

        Assert.Equal(Role.User, message.Role);
        Assert.Equal([new TextContent("hello")], message.Content);
    }

    [Fact]
    public void A_message_needs_at_least_one_piece_of_content()
    {
        Assert.Throws<ArgumentException>(() => new Message(Role.Assistant, []));
    }

    [Fact]
    public void A_message_rejects_null_content()
    {
        Assert.Throws<ArgumentException>(() => new Message(Role.User, [null!]));
    }

    [Fact]
    public void Changing_the_source_list_does_not_change_the_message()
    {
        var content = new List<Content> { new TextContent("first") };
        var message = new Message(Role.User, content);

        content.Add(new TextContent("second"));

        Assert.Single(message.Content);
    }

    [Fact]
    public void Messages_with_the_same_role_and_content_are_equal()
    {
        Assert.Equal(Message.Assistant("same"), Message.Assistant("same"));
        Assert.NotEqual(Message.Assistant("same"), Message.User("same"));
    }

    [Fact]
    public void Message_types_have_no_setters_or_mutable_fields()
    {
        Assert.NotEmpty(MessageTypes);
        foreach (var type in MessageTypes)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(property.CanWrite, $"{type.Name}.{property.Name} can be set.");
                Assert.True(IsImmutable(property.PropertyType), $"{type.Name}.{property.Name} has the mutable type {property.PropertyType.Name}.");
            }

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.All(fields, field => Assert.True(field.IsInitOnly, $"{type.Name}.{field.Name} is not read-only."));
        }
    }

    private static bool IsImmutable(Type type) =>
        type.IsEnum
        || type == typeof(string)
        || type == typeof(JsonElement)
        || type.IsPrimitive
        || MessageTypes.Contains(type)
        || (type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>)
            && IsImmutable(type.GetGenericArguments()[0]));
}
