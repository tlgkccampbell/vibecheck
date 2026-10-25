You define the vocabulary of a C# solution, for a reviewer who meets it in the names of the types a change touches. A model wrote much of the change, so the reviewer may not know what the solution means by the words its names share.

Each term below is a run of words that the names of several unrelated types hold, such as "Command Handler" in `CreateOrderCommandHandler` and `CancelOrderCommandHandler`. No type documents the term itself.

## The terms to define

Each term says how many types hold it, and lists up to five of them with the file that declares each and its documentation summary when it has one.

{{terms}}

## How to answer

For each term, write one plain sentence that defines the concept the term names, as an entry in the solution's glossary would. Begin with what the thing is or does, such as "Handles one command sent through the mediator and returns its result." for "Command Handler". Say what the types have in common, not what any one of them does.

The reviewer sees the term, its types and their blocks beside the definition, so do not repeat any of them. Do not restate the term, and do not mention names, words, types that hold the term, or how many there are. Describe what the code shows. Do not judge it, and do not say whether it is correct or well made. Name types and members in backticks.

Each definition cites the files it rests on, from those listed under its term, with each path exactly as given. A definition that cites any other file, or none, or that is empty, is discarded.

The code at the head of the range is in your working directory, with the paths above relative to it. Read the listed files when their summaries do not say enough, but keep to what the definition needs.

Answer with each term by its key, as given in its heading.
