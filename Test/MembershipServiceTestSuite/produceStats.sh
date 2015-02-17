#!/bin/bash

# where interpretor executable is located (default: current directory)
INTERPRETATOR_DIR=.

echo -n "Producing statistics ... "
# find the decision time, i.e., when silos descided to live or die
grep "sets deci" *.log | head -n 1 | cut -f14 -d' ' > first.txt
# find the time each silo discovered first failure
for f in *.log; do grep "status D" $f | head -n 1 | cut -f2 -d' '; done >> first.txt
# produce "time #silos" rows
$INTERPRETATOR_DIR/ResultsInterpretor.exe first.txt > first
rm -f first.txt

# find the decision time, i.e., when nodes descided to live or die
grep "sets deci" *.log | head -n 1 | cut -f14 -d' ' > last.txt
# find the time each silo discovered last failure
for f in *.log; do grep "status D" $f | tail -n 1 | cut -f2 -d' '; done >> last.txt
# produce "time #silos" rows
$INTERPRETATOR_DIR/ResultsInterpretor.exe last.txt > last
rm -f last.txt
echo "Done"

# produce "time #silos-know-about-first-failure #silos-know-about-last-failure" rows
echo -n "Merging results into final.cls ... "
dos2unix -q first last
# count the number of rows (=time until all silos discovered first failure)
numLinesInFirst=`cat first | wc -l`
# find the time all silos discovered first failure
lastValueInFirst=`tail -1 first | cut -f2 -d' '`
# count the number of rows (=time until all silos discovered last failure)
numLinesInLast=`cat last | wc -l`

echo "time (s),first failure,last failure" > final.csv
echo "0,0,0" >> final.csv

# merge data from "first" and "last", i.e., given "7 11" and "7 13" create a line "7,11,13"
for (( l=1; l<=$numLinesInLast; l++ ))
do
	if [ $numLinesInFirst -gt $l ]
	then
		echo $l,`head -n $l first | tail -1 | cut -f2 -d' '`,`head -n $l last | tail -1 | cut -f2 -d' '` >> final.csv
	else
		echo $l,$lastValueInFirst,`head -n $l last | tail -1 | cut -f2 -d' '` >> final.csv
	fi
done
	
rm -f first last
echo "Done"