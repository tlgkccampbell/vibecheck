You name the roles that many types of a C# solution play, for a reviewer who compares a new or changed type with the others that play the same role. The tool found the role from what the types declare about themselves: every one of them carries all of the keys below. A key is a base class the type inherits, an interface it implements, or an attribute it carries, written with the type's full name in open form.

{{keys}}

{{count}} types carried these keys before the change under review. These are three of the most typical of them, as the code before the change has them:

{{samples}}

## How to answer

Give the role a short name, as the team would say it in conversation, such as "Consumer", "API controller" or "Validator". Keep it to a few words and at most 40 characters, in the singular, and use the solution's own words where its types have them.

Then write its purpose in one plain sentence that says what an instance of the role does, such as "Handles one message from the bus and records its outcome." Describe what the code shows the types doing. Do not judge them, and do not say whether any of them is correct or well made. Name types and members in backticks.

Cite the files of the three types above that the answer rests on, with each path exactly as given. An answer that cites any other file, or none, is discarded.
