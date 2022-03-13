using FluentAssertions;
using IntroductionToEventSourcing.BusinessLogic.Tools;
using Xunit;

namespace IntroductionToEventSourcing.BusinessLogic.Immutable;

// EVENTS
public record ShoppingCartOpened(
    Guid ShoppingCartId,
    Guid ClientId
);

public record ProductItemAddedToShoppingCart(
    Guid ShoppingCartId,
    PricedProductItem ProductItem
);

public record ProductItemRemovedFromShoppingCart(
    Guid ShoppingCartId,
    PricedProductItem ProductItem
);

public record ShoppingCartConfirmed(
    Guid ShoppingCartId,
    DateTime ConfirmedAt
);

public record ShoppingCartCanceled(
    Guid ShoppingCartId,
    DateTime CanceledAt
);

// VALUE OBJECTS

public record ProductItem(
    Guid ProductId,
    int Quantity
);

public record PricedProductItem(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice
);


public static class ShoppingCartExtensions
{
    public static ShoppingCart GetShoppingCart(this IEnumerable<object> events) =>
        events.Aggregate(ShoppingCart.Default(), ShoppingCart.When);
}

// Business logic

public class OptimisticConcurrencyTests: EventStoreDBTest
{
    [Fact]
    [Trait("Category", "SkipCI")]
    public async Task GettingState_ForSequenceOfEvents_ShouldSucceed()
    {
        var shoppingCartId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var shoesId = Guid.NewGuid();
        var tShirtId = Guid.NewGuid();
        var twoPairsOfShoes = new ProductItem(shoesId, 2);
        var tShirt = new ProductItem(tShirtId, 1);

        var shoesPrice = 100;
        var tShirtPrice = 50;

        // Open
        await EventStore.Add<ShoppingCart, OpenShoppingCart>(
            command => command.ShoppingCartId,
            OpenShoppingCart.Handle,
            OpenShoppingCart.From(shoppingCartId, clientId),
            CancellationToken.None
        );

        // Try to open again
        // Should fail as stream was already created
        var exception = Record.ExceptionAsync(async () =>
            {
                await EventStore.Add<ShoppingCart, OpenShoppingCart>(
                    command => command.ShoppingCartId,
                    OpenShoppingCart.Handle,
                    OpenShoppingCart.From(shoppingCartId, clientId),
                    CancellationToken.None
                );
            }
        );
        exception.Should().BeOfType<InvalidOperationException>();

        // Add two pairs of shoes
        await EventStore.GetAndUpdate<ShoppingCart, AddProductItemToShoppingCart>(
            command => command.ShoppingCartId,
            (command, shoppingCart) =>
                AddProductItemToShoppingCart.Handle(FakeProductPriceCalculator.Returning(shoesPrice), command, shoppingCart),
            AddProductItemToShoppingCart.From(shoppingCartId, twoPairsOfShoes),
            2,
            CancellationToken.None
        );

        // Add T-Shirt
        // Should fail because of sending the same expected version as previous call
        exception = Record.ExceptionAsync(async () =>
            {
                await EventStore.GetAndUpdate<ShoppingCart, AddProductItemToShoppingCart>(
                    command => command.ShoppingCartId,
                    (command, shoppingCart) =>
                        AddProductItemToShoppingCart.Handle(FakeProductPriceCalculator.Returning(tShirtPrice), command, shoppingCart),
                    AddProductItemToShoppingCart.From(shoppingCartId, tShirt),
                    2,
                    CancellationToken.None
                );
            }
        );
        exception.Should().BeOfType<InvalidOperationException>();

        var shoppingCart = await EventStore.Get<ShoppingCart>(shoppingCartId, CancellationToken.None);

        shoppingCart.Id.Should().Be(shoppingCartId);
        shoppingCart.ClientId.Should().Be(clientId);
        shoppingCart.ProductItems.Should().HaveCount(1);
        shoppingCart.Status.Should().Be(ShoppingCartStatus.Pending);

        shoppingCart.ProductItems[0].ProductId.Should().Be(shoesId);
        shoppingCart.ProductItems[0].Quantity.Should().Be(twoPairsOfShoes.Quantity);
        shoppingCart.ProductItems[0].UnitPrice.Should().Be(shoesPrice);
    }
}