<?php
// this example is for e107 v1.1
// replace the send_file function in the request.php for this:
function send_file($file) 
{
	global $pref, $DOWNLOADS_DIRECTORY,$FILES_DIRECTORY, $e107;
	if (!$pref['download_php'])
	{
		header("Location: ".SITEURL.$file);
		exit();
	}

	@session_write_close();
	while (@ob_end_clean());
	$filename = str_replace('/', '\\', $file);
	$file = basename($file);
	$path = realpath($filename);
	$path_downloads = realpath($DOWNLOADS_DIRECTORY);
	$path_public = realpath($FILES_DIRECTORY."public/");

	if(!strstr($path, $path_downloads) && !strstr($path,$path_public)) 
	{
		if(E107_DEBUG_LEVEL > 0 && ADMIN)
		{
			echo "Failed to Download <b>".$file."</b><br />";
			echo "The file-path <b>".$path."<b> didn't match with either <b>{$path_downloads}</b> or <b>{$path_public}</b><br />";
			exit();
        }
		else
		{
			header("location: {$e107->base_path}");
			exit();
		}
	}
	else
	{
		header("X-Sendfile: ".$filename);
		header("X-E107-Redirect-To: ".e_BASE."index.php");
		exit();
	}
}
