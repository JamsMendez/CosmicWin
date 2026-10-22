namespace CosmicWin.Layout;

/// <summary>
/// Which half of a tile a node is stacked into by <see cref="LayoutTree.StackBeside"/>: the top
/// (<see cref="Above"/>) or the bottom (<see cref="Below"/>). Up/down only -- dropping a window
/// beside a tile on the left or right was not asked for.
/// </summary>
public enum StackSide
{
    Above,
    Below
}
